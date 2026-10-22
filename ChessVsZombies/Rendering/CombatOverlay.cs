using System.Globalization;
using ChessVsZombies.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessVsZombies.Rendering;

/// <summary>Camera-facing status bars and transient damage labels, drawn beneath the command UI.</summary>
public static class CombatOverlay
{
    public static void Draw(GameSession session, SpriteBatch batch, Texture2D pixel, UiFont font,
        Viewport viewport, Matrix view, Matrix projection, float uiScale)
    {
        var camera = Matrix.Invert(view).Translation;
        bool Project(Vector3 world, out Vector2 screen)
        {
            var clip = Vector4.Transform(new Vector4(world, 1), view * projection);
            screen = default;
            if (clip.W <= 0 || clip.Z < 0 || clip.Z > clip.W) return false;
            var projected = viewport.Project(world, projection, view, Matrix.Identity);
            screen = new Vector2(projected.X, projected.Y) / uiScale;
            return projected.X is >= -120 && projected.X <= viewport.Width + 120 &&
                projected.Y is >= -120 && projected.Y <= viewport.Height + 120;
        }
        bool Visible(Vector3 point)
        {
            var delta = point - camera;
            var distance = delta.Length();
            if (distance < .1f) return true;
            return WorldPicker.Pick(session, new System.Numerics.Vector3(camera.X, camera.Y, camera.Z),
                new System.Numerics.Vector3(delta.X, delta.Y, delta.Z), distance - .1f, includeLiquids: false) is null;
        }
        void Fill(float x, float y, float width, float height, Color color) =>
            batch.Draw(pixel, new Rectangle((int)x, (int)y, Math.Max(0, (int)width), (int)height), color);
        void Bar(string label, float value, float maximum, Color color, float x, ref float y)
        {
            Fill(x - 2, y, 108, 12, new Color(10, 16, 23, 220));
            font.Draw(batch, label, new Vector2(x + 1, y - 1), color, 9);
            Fill(x + 36, y + 3, 66, 5, new Color(51, 60, 67));
            Fill(x + 36, y + 3, 66 * Math.Clamp(value / Math.Max(1, maximum), 0, 1), 5, color);
            y += 12;
        }
        // Far bars first; nearby bars remain readable where silhouettes overlap.
        foreach (var zombie in session.Zombies.Where(z => z.Hp > 0)
                     .OrderByDescending(z => Vector3.DistanceSquared(camera, WorldRenderer.ToXna(z.Position))))
        {
            var position = WorldRenderer.ToXna(zombie.Position);
            if (!Project(position + Vector3.Up * (.65f * zombie.BodyScale), out var anchor) ||
                !Visible(position + Vector3.Up * (.35f * zombie.BodyScale))) continue;
            int rows = 2 + zombie.ArmorPieces.Count + (zombie.IsGiga ? 2 : 0) + (zombie.AirRemaining < Balance.AirSeconds ? 1 : 0);
            var x = anchor.X - 52;
            var y = anchor.Y - rows * 12;
            var name = zombie.Size switch
            {
                ZombieSize.Micro => "MICRO",
                ZombieSize.Milli => "MILLI",
                ZombieSize.Kilo => "KILO",
                ZombieSize.Mega => "MEGA",
                ZombieSize.Giga => "GIGA",
                _ => "STANDARD"
            };
            Fill(x - 2, y, 108, 12, new Color(10, 16, 23, 220));
            font.Draw(batch, name, new Vector2(x + 1, y - 1), new Color(105, 225, 191), 9);
            y += 12;
            Bar("HP", zombie.BodyHp, zombie.MaxBodyHp, new Color(242, 134, 115), x, ref y);
            if (zombie.Giga is { } giga)
            {
                foreach (var (label, number, color) in new[] {
                    ("RES", giga.Resistance.ToString(giga.Resistance >= 100_000 ? "0.##e+0" : "0.##", CultureInfo.InvariantCulture), new Color(134, 218, 235)),
                    ("ATK", GigaAttackLabel(giga.AttackCounter), new Color(255, 162, 92)) })
                {
                    Fill(x - 2, y, 108, 12, new Color(10, 16, 23, 220));
                    font.Draw(batch, label, new Vector2(x + 1, y - 1), color, 9);
                    float numberSize = Math.Min(9, 9 * 65 / Math.Max(1, font.Measure(number, 9)));
                    font.Draw(batch, number, new Vector2(x + 37, y - 1), color, numberSize);
                    y += 12;
                }
            }
            foreach (var slot in new[] { ArmorSlot.Helmet, ArmorSlot.Chestplate, ArmorSlot.Shield, ArmorSlot.Boots })
            {
                var piece = zombie.ArmorPieces.FirstOrDefault(p => p.Slot == slot);
                if (piece is null) continue;
                var label = slot switch { ArmorSlot.Helmet => "HELM", ArmorSlot.Chestplate => "CHEST", ArmorSlot.Boots => "BOOTS", _ => "SHIELD" };
                var color = slot == ArmorSlot.Helmet ? new Color(240, 200, 113) :
                    slot == ArmorSlot.Chestplate ? new Color(126, 209, 223) :
                    slot == ArmorSlot.Boots ? new Color(178, 220, 145) : new Color(182, 160, 239);
                Bar(label, piece.Hp, piece.MaxHp, color, x, ref y);
            }
            if (zombie.AirRemaining < Balance.AirSeconds)
                Bar("AIR", zombie.AirRemaining, Balance.AirSeconds, new Color(107, 185, 231), x, ref y);
        }
        foreach (var hit in session.Feedback.Hits)
        {
            var target = session.Zombies.FirstOrDefault(z => z.Id == hit.ZombieId);
            float bodyScale = target?.BodyScale ?? 1;
            var point = WorldRenderer.ToXna(hit.Position) + Vector3.Up * (.9f * bodyScale + hit.Age * .8f);
            if (!Project(point, out var anchor) || !Visible(WorldRenderer.ToXna(hit.Position) + Vector3.Up * (.35f * bodyScale))) continue;
            var amount = "-" + hit.Amount.ToString("0.##", CultureInfo.InvariantCulture);
            var color = hit.Kind == DamageKind.Chemical ? new Color(178, 244, 139) :
                hit.Kind == DamageKind.Thermal ? new Color(255, 185, 106) : Color.White;
            float opacity = Math.Clamp((CombatFeedback.NumberDuration - hit.Age) / .3f, 0, 1);
            int statusRows = target is null ? 1 : 2 + target.ArmorPieces.Count + (target.IsGiga ? 2 : 0) + (target.AirRemaining < Balance.AirSeconds ? 1 : 0);
            var position = anchor + new Vector2((hit.Id % 3 - 1) * 30 - font.Measure(amount, 18) * .5f, -12 - statusRows * 12);
            // This pass uses straight alpha; fading RGB too would darken the label twice.
            font.Draw(batch, amount, position + Vector2.One, new Color(8, 12, 18, (int)(255 * opacity)), 18);
            font.Draw(batch, amount, position, new Color(color.R, color.G, color.B, (byte)(255 * opacity)), 18);
        }
    }

    private static string GigaAttackLabel(int counter)
    {
        // Format from the exponent directly: a legitimate large ATK can exceed
        // floating-point range while its logarithm remains finite.
        double logarithm = counter * Math.Log10(GigaRules.GoldenRatio);
        if (logarithm < 5) return Math.Pow(10, logarithm).ToString("0.##", CultureInfo.InvariantCulture);
        long exponent = (long)Math.Floor(logarithm);
        double mantissa = Math.Round(Math.Pow(10, logarithm - exponent), 2);
        if (mantissa >= 10) { mantissa = 1; exponent++; }
        return mantissa.ToString("0.##", CultureInfo.InvariantCulture) + "e+" + exponent.ToString(CultureInfo.InvariantCulture);
    }
}
