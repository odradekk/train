using Chickensoft.GoDotTest;
using Godot;
using Train.App.Assets;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class SceneMaterialPixelTests
{
    [Test]
    public async Task Missing_face_uv_reads_the_same_zero_values_as_an_explicit_zero_channel()
    {
        using var fixture = new PixelFixture(TestScene);
        using var omitted = await fixture.ReadAttributes(false, null, null);
        using var zero = await fixture.ReadAttributes(false, new Vector2[4], null);
        using var nonzero = await fixture.ReadAttributes(false, Enumerable.Repeat(new Vector2(0.25f, 0.5f), 4).ToArray(), null);
        Require(omitted.GetPixel(72, 72).A > 0.99f && AllEqual(omitted, zero),
            "省略 UV2 必须实际绘制，并与显式零通道的着色器读值相同。");
        var control = nonzero.GetPixel(72, 72);
        var original = zero.GetPixel(72, 72);
        Require(control.R - original.R > 0.03f && control.G - original.G > 0.03f,
            "非零 UV2 对照必须分别改变两个输出通道，证明着色器确实读取属性。");
    }

    [Test]
    public async Task Missing_custom_reads_the_same_zero_xyz_as_an_explicit_zero_channel()
    {
        using var fixture = new PixelFixture(TestScene);
        using var omitted = await fixture.ReadAttributes(true, null, null);
        using var zero = await fixture.ReadAttributes(true, null, new float[16]);
        using var nonzero = await fixture.ReadAttributes(true, null,
            Enumerable.Range(0, 4).SelectMany(_ => new[] { 0.25f, 0.5f, 0.75f, 0f }).ToArray());
        Require(omitted.GetPixel(72, 72).A > 0.99f && AllEqual(omitted, zero),
            "省略 CUSTOM0 必须实际绘制，并与显式零通道的 xyz 着色器读值相同。");
        var control = nonzero.GetPixel(72, 72);
        var original = zero.GetPixel(72, 72);
        Require(control.R - original.R > 0.03f && control.G - original.G > 0.03f && control.B - original.B > 0.03f,
            "非零 CUSTOM0 对照必须分别改变三个输出通道，证明着色器确实读取属性。");
    }

    private sealed partial class PixelFixture
    {
        internal async Task<Image> ReadAttributes(bool custom, Vector2[]? faceUvs, float[]? values)
        {
            using var mesh = new ArrayMesh();
            MeshSurfaces.Add(mesh, Mesh.PrimitiveType.Triangles,
                flags: (Mesh.ArrayFormat)((ulong)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift),
                vertices: [new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0)],
                indices: [0, 2, 1, 0, 3, 2], faceUvs: faceUvs, custom: values);
            using var shader = new Shader
            {
                Code = """
                    shader_type spatial;
                    render_mode cull_disabled;
                    uniform bool read_custom;
                    varying vec3 attribute_values;
                    void vertex() { attribute_values = read_custom ? CUSTOM0.xyz : vec3(UV2, 0.0); }
                    void fragment() { ALBEDO = vec3(0.1) + attribute_values * 0.5; }
                    """
            };
            using var material = new ShaderMaterial { Shader = shader };
            material.SetShaderParameter("read_custom", custom);
            return await Render(mesh, material);
        }
    }
}
