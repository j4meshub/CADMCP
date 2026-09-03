using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using CADMCP.Plugin;

namespace CADMCP.CommandSet;

internal static class ModifyTargetGuard
{
    private static readonly HashSet<Type> Supported = new()
    {
        typeof(Line), typeof(Arc), typeof(Circle), typeof(Polyline), typeof(Solid), typeof(DBText), typeof(MText),
        typeof(BlockReference), typeof(Leader), typeof(MLeader), typeof(RadialDimension), typeof(DiametricDimension),
        typeof(AlignedDimension), typeof(RotatedDimension), typeof(DBPoint), typeof(Hatch)
    };

    public static void Validate(Entity entity, Transaction tr)
    {
        var h = entity.Handle.ToString();
        if (!Supported.Contains(entity.GetType())) Fail("unsupported_entity_type", h, "不支持的顶层类型: " + entity.GetType().Name);
        var layer = Resource<LayerTableRecord>(entity.LayerId, tr, h);
        if (layer.IsLocked) Fail("layer_locked", h, "图层已锁定");
        if (!entity.Visible || layer.IsOff || layer.IsFrozen) Fail("entity_not_selectable", h, "实体不可见或图层关闭/冻结");
        Resource<LinetypeTableRecord>(entity.LinetypeId, tr, h);
        if (entity.Annotative == AnnotativeStates.True) Fail("unsupported_entity_type", h, "暂不支持注释性对象");
        // Conservative: never silently drag other objects through association/group/custom reactors.
        if (AssocDependency.GetDependenciesOnObject(entity, true, true).Count > 0 || entity.GetPersistentReactorIds().Count > 0)
            Fail("dependent_entity", h, "存在关联、组或持久反应器依赖");
        if (!entity.ExtensionDictionary.IsNull)
        {
            var dictionary = (DBDictionary)tr.GetObject(entity.ExtensionDictionary, OpenMode.ForRead);
            if (dictionary.Contains("ACAD_DIMASSOC")) Fail("dependent_entity", h, "存在尺寸关联依赖");
        }
        if (entity is Hatch hatch && hatch.Associative) Fail("dependent_entity", h, "暂不支持关联填充");
        if (entity is Leader leader && !leader.Annotation.IsNull) Fail("dependent_entity", h, "引线关联外部注释对象");
        if (entity is DBText text) Resource<TextStyleTableRecord>(text.TextStyleId, tr, h);
        if (entity is MText mtext) Resource<TextStyleTableRecord>(mtext.TextStyleId, tr, h);
        if (entity is Dimension dim) Resource<DimStyleTableRecord>(dim.DimensionStyle, tr, h);
        if (entity is Leader l) Resource<DimStyleTableRecord>(l.DimensionStyle, tr, h);
        if (entity is BlockReference block)
        {
            var record = Resource<BlockTableRecord>(block.BlockTableRecord, tr, h);
            if (record.IsFromExternalReference || record.IsFromOverlayReference || record.IsDependent)
                Fail("dependent_entity", h, "暂不支持外部参照");
            var definition = block.IsDynamicBlock ? Resource<BlockTableRecord>(block.DynamicBlockTableRecord, tr, h) : record;
            if (definition.Annotative == AnnotativeStates.True) Fail("unsupported_entity_type", h, "暂不支持注释性块");
            foreach (ObjectId id in block.AttributeCollection)
            {
                var attribute = Resource<AttributeReference>(id, tr, h);
                Resource<TextStyleTableRecord>(attribute.TextStyleId, tr, h);
                if (Resource<LayerTableRecord>(attribute.LayerId, tr, h).IsLocked)
                    Fail("layer_locked", h, "块属性位于锁定图层");
                if (attribute.Annotative == AnnotativeStates.True || attribute.GetPersistentReactorIds().Count > 0)
                    Fail("dependent_entity", h, "块属性存在不支持的注释/关联依赖");
            }
        }
    }
    private static T Resource<T>(ObjectId id, Transaction tr, string handle) where T : DBObject
    {
        if (id.IsNull || !id.IsValid || id.IsErased) throw new CadCommandException("missing_resource", handle + ": 所需资源失效");
        return tr.GetObject(id, OpenMode.ForRead, false) as T ?? throw new CadCommandException("missing_resource", handle + ": 资源类型不匹配");
    }
    private static void Fail(string code, string handle, string message) => throw new CadCommandException(code, handle + ": " + message);
}
