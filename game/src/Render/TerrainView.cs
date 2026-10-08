using System;
using Godot;

namespace Hearthspan.Game.Render;

/// <summary>Plan v5 M2: the ground mesh (in chunks, so off-screen parts cull) and the sea surface, both
/// shaded from <see cref="MapTextures"/>. Built once per game; per-turn changes are uniform/texture
/// updates only.</summary>
public partial class TerrainView : Node3D
{
    private const int ChunkQuads = 48;

    private ShaderMaterial? _terrainMaterial;
    private ShaderMaterial? _waterMaterial;

    public void Build(TerrainModel model, MapTextures textures, Color[] civPalette)
    {
        foreach (var child in GetChildren()) child.QueueFree();

        _terrainMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/terrain.gdshader") };
        _terrainMaterial.SetShaderParameter("fog_tex", textures.Fog);
        _terrainMaterial.SetShaderParameter("paint_tex", textures.Paint);
        _terrainMaterial.SetShaderParameter("hex_data", textures.HexData);
        _terrainMaterial.SetShaderParameter("world_rect", textures.WorldRect);
        _terrainMaterial.SetShaderParameter("grid_size", new Vector2(model.Cols, model.Rows));
        var palette = new Godot.Collections.Array();
        for (int i = 0; i < 8; i++) palette.Add(civPalette[i % civPalette.Length]);
        _terrainMaterial.SetShaderParameter("civ_colors", palette);

        for (int cz = 0; cz < model.NZ - 1; cz += ChunkQuads)
        {
            for (int cx = 0; cx < model.NX - 1; cx += ChunkQuads)
            {
                var mesh = BuildChunk(model, cx, cz, Math.Min(cx + ChunkQuads, model.NX - 1), Math.Min(cz + ChunkQuads, model.NZ - 1));
                AddChild(new MeshInstance3D
                {
                    Name = $"Ground_{cx}_{cz}",
                    Mesh = mesh,
                    MaterialOverride = _terrainMaterial,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
                });
            }
        }

        _waterMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/water.gdshader") };
        _waterMaterial.SetShaderParameter("height_tex", textures.Height);
        _waterMaterial.SetShaderParameter("fog_tex", textures.Fog);
        _waterMaterial.SetShaderParameter("world_rect", textures.WorldRect);
        _waterMaterial.SetShaderParameter("wave_a", WaveNormals(3, 0.9f));
        _waterMaterial.SetShaderParameter("wave_b", WaveNormals(11, 1.6f));
        float cxWorld = (model.MinX + model.MaxX) * 0.5f, czWorld = (model.MinZ + model.MaxZ) * 0.5f;
        AddChild(new MeshInstance3D
        {
            Name = "Sea",
            Mesh = new PlaneMesh { Size = new Vector2(900f, 900f) },
            Position = new Vector3(cxWorld, 0f, czWorld),
            MaterialOverride = _waterMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }

    private static NoiseTexture2D WaveNormals(int seed, float frequencyScale) => new()
    {
        Width = 256,
        Height = 256,
        Seamless = true,
        AsNormalMap = true,
        BumpStrength = 3.5f,
        GenerateMipmaps = true,
        Noise = new FastNoiseLite
        {
            Seed = seed,
            NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex,
            Frequency = 0.012f * frequencyScale,
            FractalOctaves = 3,
        },
    };

    private static ArrayMesh BuildChunk(TerrainModel m, int x0, int z0, int x1, int z1)
    {
        int w = x1 - x0 + 1, h = z1 - z0 + 1;
        var verts = new Vector3[w * h];
        var normals = new Vector3[w * h];
        var colors = new Color[w * h];
        for (int z = 0; z < h; z++)
        {
            for (int x = 0; x < w; x++)
            {
                int gi = (z0 + z) * m.NX + (x0 + x);
                int li = z * w + x;
                verts[li] = new Vector3(m.MinX + (x0 + x) * TerrainModel.Spacing, m.Heights[gi], m.MinZ + (z0 + z) * TerrainModel.Spacing);
                normals[li] = m.Normals[gi];
                colors[li] = m.Colors[gi];
            }
        }

        var indices = new int[(w - 1) * (h - 1) * 6];
        int k = 0;
        for (int z = 0; z < h - 1; z++)
        {
            for (int x = 0; x < w - 1; x++)
            {
                int i00 = z * w + x, i10 = i00 + 1, i01 = i00 + w, i11 = i01 + 1;
                // Godot's front faces wind clockwise seen from the viewer; the camera looks down on
                // the XZ plane (+X right, +Z toward the bottom of the screen).
                indices[k++] = i00; indices[k++] = i10; indices[k++] = i01;
                indices[k++] = i10; indices[k++] = i11; indices[k++] = i01;
            }
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    public void SetGridAlpha(float alpha) => _terrainMaterial?.SetShaderParameter("grid_alpha", alpha);

    public void SetSelectedHex(int col, int row, bool enabled) =>
        _terrainMaterial?.SetShaderParameter("selected_hex", new Vector4(col, row, enabled ? 1f : 0f, 0f));

    public void SetHoverHex(int col, int row, bool enabled) =>
        _terrainMaterial?.SetShaderParameter("hover_hex", new Vector4(col, row, enabled ? 1f : 0f, 0f));
}
