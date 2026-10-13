using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessVsZombies.Rendering;

public sealed class UiFont : IDisposable
{
    private sealed class Glyph
    {
        public int Code { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public float Advance { get; set; }
    }
    private readonly Texture2D texture;
    private readonly Dictionary<int, Glyph> glyphs;
    public UiFont(GraphicsDevice device)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Assets");
        using var stream = File.OpenRead(Path.Combine(directory, "ui-font.png"));
        texture = Texture2D.FromStream(device, stream);
        glyphs = JsonSerializer.Deserialize<Glyph[]>(File.ReadAllText(Path.Combine(directory, "ui-font.json")))!.ToDictionary(g => g.Code);
    }
    public float Measure(string text, float size = 20) => text.Sum(c => glyphs.GetValueOrDefault(c)?.Advance ?? glyphs[63].Advance) * size / 26f;
    public void Draw(SpriteBatch batch, string text, Vector2 position, Color color, float size = 20)
    {
        var startX = position.X;
        float scale = size / 26f;
        foreach (char c in text)
        {
            if (c == '\n') { position.X = startX; position.Y += size * 1.4f; continue; }
            var g = glyphs.GetValueOrDefault(c) ?? glyphs[63];
            batch.Draw(texture, position - new Vector2(4 * scale, 0), new Rectangle(g.X, g.Y, g.Width, 40), color, 0, Vector2.Zero, scale, SpriteEffects.None, 0);
            position.X += g.Advance * scale;
        }
    }
    public void Dispose() => texture.Dispose();
}
