using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace CADMCP.CommandSet;

// Row-major affine matrices, also used by the pure test executable.
internal static class TransformMath
{
    public static double[] Identity() => new double[] { 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1 };
    public static double[] Multiply(double[] a, double[] b)
    {
        var value = new double[16];
        for (var r = 0; r < 4; r++) for (var c = 0; c < 4; c++)
        { for (var k = 0; k < 4; k++) value[r * 4 + c] += a[r * 4 + k] * b[k * 4 + c]; ModifyRequest.Finite(value[r * 4 + c]); }
        return value;
    }
    public static double[] Point(double[] matrix, double[] point)
    {
        var result = new double[3];
        for (var r = 0; r < 3; r++)
        { result[r] = matrix[r * 4 + 3]; for (var k = 0; k < 3; k++) result[r] += matrix[r * 4 + k] * point[k]; ModifyRequest.Finite(result[r]); }
        return result;
    }
    public static double[] InFrame(double[] local, double[] frame, double[] inverseFrame) => Multiply(Multiply(frame, local), inverseFrame);
    private static double[] Offset(double[] p)
    { var m = Identity(); m[3] = p[0]; m[7] = p[1]; m[11] = p[2]; return m; }
    private static double[] At(double[] m, double[] p) => Multiply(Multiply(Offset(p), m), Offset(p.Select(x => -x).ToArray()));

    public static double[] Local(JObject operation, Func<double, double> toNative)
    {
        var type = ModifyRequest.String(operation["type"], "operation.type"); var m = Identity();
        double[] Read(string name) => ModifyRequest.Point(operation[name], name).Select(x => { var v = toNative(x); ModifyRequest.Finite(v); return v; }).ToArray();
        switch (type)
        {
            case "move":
                ModifyRequest.Keys(operation, "type", "displacement"); return Offset(Read("displacement"));
            case "rotate":
                ModifyRequest.Keys(operation, "type", "basePoint", "angle");
                var angle = ModifyRequest.Number(operation["angle"], "angle"); var c = Math.Cos(angle); var s = Math.Sin(angle);
                m[0] = c; m[1] = -s; m[4] = s; m[5] = c; return At(m, Read("basePoint"));
            case "scale":
                ModifyRequest.Keys(operation, "type", "basePoint", "factor");
                var factor = ModifyRequest.Number(operation["factor"], "factor");
                if (factor <= 0) throw ModifyRequest.Invalid("factor 必须大于零");
                m[0] = m[5] = m[10] = factor; return At(m, Read("basePoint"));
            case "mirror":
                ModifyRequest.Keys(operation, "type", "axisStart", "axisEnd");
                var a = Read("axisStart"); var b = Read("axisEnd");
                if (a[2] != b[2]) throw ModifyRequest.Invalid("镜像轴两点的输入 Z 必须相同");
                var dx = b[0] - a[0]; var dy = b[1] - a[1];
                var size = Math.Max(Math.Abs(dx), Math.Abs(dy)); ModifyRequest.Finite(size);
                if (size == 0) throw ModifyRequest.Invalid("镜像轴两点不能重合");
                dx /= size; dy /= size; var length = Math.Sqrt(dx * dx + dy * dy); dx /= length; dy /= length;
                m[0] = 2 * dx * dx - 1; m[1] = m[4] = 2 * dx * dy; m[5] = 2 * dy * dy - 1;
                return At(m, a); // Mirror plane contains the axis and local Z; never negate elevation.
            default: throw ModifyRequest.Invalid("operation.type 只能为 move/rotate/scale/mirror");
        }
    }
}
