using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CADMCP.Plugin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CADMCP.CommandSet;

public sealed class CloneEntitiesCommand : AtomicModifyCommand { public override string Name => "clone_entities"; protected override bool IsClone => true; }
public sealed class TransformEntitiesCommand : AtomicModifyCommand { public override string Name => "transform_entities"; protected override bool IsClone => false; }

public abstract class AtomicModifyCommand : ICadCommand
{
    public abstract string Name { get; }
    public CadExecutionKind ExecutionKind => CadExecutionKind.PreviewableWrite;
    protected abstract bool IsClone { get; }
    protected virtual void TransformEntity(Entity entity, Matrix3d matrix) => entity.TransformBy(matrix);
    private const int ResponseBudget = 7 * 1024 * 1024;

    public JObject Execute(CadCommandContext context, JObject parameters)
    {
        var watch = Stopwatch.StartNew(); var warnings = new JArray();
        JObject? response = null; var writeStarted = false; var committed = false;
        try
        {
            // Reject direct/expired/worker calls before touching CAD. This validates an
            // existing native scope; it does not open an undo group or modify the DWG.
            if (CommandExecutionPolicy.Resolve(ExecutionKind, parameters) == CadExecutionKind.CommandContext)
                StrictUndoBoundary.Require(context);
            EntityTargetResolver.ValidateContext(context, parameters, IsClone
                ? new[] { "source", "expectedCount", "coordinateSystem", "dryRun", "displacement", "selectCreated" }
                : new[] { "source", "expectedCount", "coordinateSystem", "dryRun", "operation" });
            var request = ModifyRequest.Parse(parameters, context.InitialSelectionHandles.ToArray(), IsClone);
            var db = context.Document.Database; var units = new CadUnits(db, context.Settings);
            var local = TransformMath.Local(request.Operation, units.FromMillimeters);
            var frame = context.Document.Editor.CurrentUserCoordinateSystem;
            var matrix = new Matrix3d(request.CoordinateSystem == "wcs" ? local : TransformMath.InFrame(local, frame.ToArray(), frame.Inverse().ToArray()));
            if (request.Operation.Value<string>("type") == "mirror")
                warnings.Add(new JObject { ["code"] = "native_text_mirror", ["message"] = "文字镜像沿用 AutoCAD 原生行为及当前 MIRRTEXT；不强制反字，不修改 MIRRTEXT 或共享块定义" });

            // A preview must never acquire a default/write lock, even when called directly.
            // A read lock cannot wrap the real write path (nor be upgraded implicitly).
            using (context.Document.LockDocument(request.DryRun
                ? Autodesk.AutoCAD.ApplicationServices.DocumentLockMode.Read
                : Autodesk.AutoCAD.ApplicationServices.DocumentLockMode.Write, null, null, false))
            {
                // Fully read/validate before opening an undo boundary or a write transaction.
                using (var read = db.TransactionManager.StartOpenCloseTransaction())
                {
                    var entities = ResolveValidated(db, read, request.Handles);
                    var preview = BuildPreview(context, request, entities, matrix, units);
                    CheckResponse(ExecutionResponses.Success(context.CallId, preview, 0, null, false, false, warnings));
                    if (request.DryRun)
                        return ExecutionResponses.Success(context.CallId, preview, watch.ElapsedMilliseconds, null, false, false, warnings);
                }
                StrictUndoBoundary.Require(context);
                ObjectId[]? selectIds = null;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var sources = ResolveValidated(db, tr, request.Handles);
                    var before = sources.Select(e => Box(e, units)).ToArray();
                    var sourceIds = sources.Select(e => e.ObjectId).ToArray();
                    var output = new List<Entity>(); var mappingResult = new JArray(); var appended = new List<ObjectId>();
                    ObjectEventHandler capture = (_, e) => appended.Add(e.DBObject.ObjectId);
                    db.ObjectAppended += capture;
                    try
                    {
                        writeStarted = true;
                        if (IsClone)
                        {
                            var beforeIds = new HashSet<ObjectId>(((BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead)).Cast<ObjectId>());
                            using (var mapping = new IdMapping())
                            {
                                db.DeepCloneObjects(new ObjectIdCollection(sourceIds), db.CurrentSpaceId, mapping, false);
                                foreach (var source in sources)
                                {
                                    if (!mapping.Contains(source.ObjectId)) throw new CadCommandException("clone_incomplete", "缺少克隆映射: " + source.Handle);
                                    var pair = mapping[source.ObjectId];
                                    if (!pair.IsCloned || pair.Value == source.ObjectId || pair.Value.IsNull)
                                        throw new CadCommandException("clone_incomplete", "实体未生成副本: " + source.Handle);
                                    var copy = tr.GetObject(pair.Value, OpenMode.ForWrite, false) as Entity;
                                    if (copy == null || copy.OwnerId != db.CurrentSpaceId || beforeIds.Contains(copy.ObjectId))
                                        throw new CadCommandException("clone_incomplete", "副本归属或标识错误: " + source.Handle);
                                    // BlockReference.TransformBy also transforms its AttributeReferences.
                                    TransformEntity(copy, matrix); output.Add(copy);
                                    mappingResult.Add(new JObject { ["sourceHandle"] = source.Handle.ToString(), ["createdHandle"] = copy.Handle.ToString() });
                                }
                            }
                            var newIds = ((BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead)).Cast<ObjectId>().Where(id => !beforeIds.Contains(id));
                            if (!new HashSet<ObjectId>(newIds).SetEquals(output.Select(e => e.ObjectId)))
                                throw new CadCommandException("unexpected_created_entities", "克隆产生了目标范围外的顶层实体");
                        }
                        else
                        {
                            foreach (var source in sources)
                            {
                                var entity = (Entity)tr.GetObject(source.ObjectId, OpenMode.ForWrite, false);
                                TransformEntity(entity, matrix); output.Add(entity);
                            }
                        }
                        if (output.Select(e => e.ObjectId).Distinct().Count() != sources.Length)
                            throw new CadCommandException("clone_incomplete", "结果数量或唯一性不符");
                        CheckAppendedTopLevel(tr, appended, IsClone ? output.Select(e => e.ObjectId) : Enumerable.Empty<ObjectId>());
                    }
                    finally { db.ObjectAppended -= capture; }

                    var items = new JArray();
                    for (var i = 0; i < output.Count; i++)
                    {
                        var item = EntitySerialization.Summary(output[i], units, false);
                        item["beforeBoundingBoxWcs"] = before[i]; item["afterBoundingBoxWcs"] = Box(output[i], units);
                        if (IsClone) item["sourceHandle"] = request.Handles[i];
                        items.Add(item);
                    }
                    var result = Result(context, request, items);
                    if (IsClone)
                    {
                        result["createdHandles"] = new JArray(output.Select(e => e.Handle.ToString()));
                        result["handleMapping"] = mappingResult;
                        result["selectionRequested"] = request.SelectCreated; result["selectionApplied"] = false;
                        if (request.SelectCreated) selectIds = output.Select(e => e.ObjectId).ToArray();
                    }
                    else result["handles"] = new JArray(request.Handles);
                    // The outer dispatcher confirms the native undo unit after command completion.
                    response = ExecutionResponses.Success(context.CallId, result, watch.ElapsedMilliseconds, "auto", true, false, warnings);
                    CheckResponse(response); // Materialize and size-check before commit, never afterwards.
                    if (selectIds != null) context.RequestSelection(selectIds);
                    tr.Commit(); committed = true;
                }
            }
        }
        catch (Exception error)
        {
            if (!committed)
                response = ExecutionResponses.Failure(context.CallId, error is CadCommandException business ? business.Code : "modify_failed",
                    error, watch.ElapsedMilliseconds, "auto", writeStarted, null, warnings);
            else warnings.Add(new JObject { ["code"] = "post_commit_warning", ["message"] = error.Message });
        }
        response ??= ExecutionResponses.Failure(context.CallId, "modify_failed", new InvalidOperationException("缺少执行结果"));
        response["warnings"] = warnings.DeepClone();
        response["durationMs"] = watch.ElapsedMilliseconds;
        return response;
    }

    private static Entity[] ResolveValidated(Database db, Transaction tr, string[] handles)
    { var entities = EntityTargetResolver.Resolve(db, tr, handles); foreach (var entity in entities) ModifyTargetGuard.Validate(entity, tr); return entities; }
    private static void CheckResponse(JObject response)
    {
        if (Encoding.UTF8.GetByteCount(response.ToString(Formatting.None)) > ResponseBudget)
            throw new CadCommandException("result_too_large", "结果超过 7 MiB，请拆分目标；本次未提交");
    }
    private static JObject Box(Entity entity, CadUnits units)
    {
        try { var box = GeometryInput.ExtentsJson(entity.GeometricExtents, units); foreach (var v in box.Descendants().OfType<JValue>().Where(x => x.Type == JTokenType.Float)) ModifyRequest.Finite(v.Value<double>()); return box; }
        catch (CadCommandException) { throw; }
        catch (Exception error) { throw new CadCommandException("geometry_unavailable", entity.Handle + ": 包围盒不可用: " + error.Message); }
    }
    private static JObject Result(CadCommandContext context, ModifyRequest request, JArray items) => context.WithIdentity(new JObject
    {
        ["dryRun"] = request.DryRun, ["sourceHandles"] = new JArray(request.Handles), ["count"] = request.Handles.Length,
        ["items"] = items, ["operation"] = request.Operation.DeepClone(), ["inputCoordinateSystem"] = request.CoordinateSystem,
        ["coordinateSystem"] = "wcs", ["unit"] = "Millimeters"
    });
    private static JObject BuildPreview(CadCommandContext context, ModifyRequest request, Entity[] entities, Matrix3d matrix, CadUnits units)
    {
        var items = new JArray();
        foreach (var e in entities)
        {
            var before = Box(e, units); var ext = e.GeometricExtents; Extents3d? after = null;
            foreach (var x in new[] { ext.MinPoint.X, ext.MaxPoint.X }) foreach (var y in new[] { ext.MinPoint.Y, ext.MaxPoint.Y }) foreach (var z in new[] { ext.MinPoint.Z, ext.MaxPoint.Z })
            {
                var point = new Point3d(x, y, z).TransformBy(matrix);
                ModifyRequest.Finite(point.X); ModifyRequest.Finite(point.Y); ModifyRequest.Finite(point.Z);
                if (after == null) after = new Extents3d(point, point); else { var value = after.Value; value.AddPoint(point); after = value; }
            }
            items.Add(new JObject { ["handle"] = e.Handle.ToString(), ["beforeBoundingBoxWcs"] = before,
                ["predictedBoundingBoxWcs"] = GeometryInput.ExtentsJson(after!.Value, units), ["predictionKind"] = "conservative" });
        }
        return Result(context, request, items);
    }
    private static void CheckAppendedTopLevel(Transaction tr, IEnumerable<ObjectId> appended, IEnumerable<ObjectId> expected)
    {
        var allowed = new HashSet<ObjectId>(expected);
        foreach (var id in appended.Distinct())
        {
            if (id.IsNull || !id.IsValid || id.IsErased || !(tr.GetObject(id, OpenMode.ForRead, false) is Entity entity)) continue;
            if (!entity.OwnerId.IsNull && tr.GetObject(entity.OwnerId, OpenMode.ForRead, false) is BlockTableRecord record && record.IsLayout && !allowed.Contains(id))
                throw new CadCommandException("unexpected_created_entities", "出现范围外的顶层实体: " + entity.Handle);
        }
    }
}
