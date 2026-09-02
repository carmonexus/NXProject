using System.Globalization;
using System.Windows.Controls;
using System.Windows.Media;

namespace NXProject.Community.Services
{
    /// <summary>
    /// Utilitário único de seleção/conversão de cor usado por todas as telas do NX.
    /// Centraliza o ColorDialog do Windows (paleta completa) e a conversão hex ⇄ cor
    /// para não repetir (e errar) esse código em cada janela.
    /// </summary>
    public static class ColorPickerHelper
    {
        /// <summary>Abre a paleta do Windows a partir de um hex; devolve o novo hex se confirmado.</summary>
        public static bool TryPick(string? currentHex, out string newHex)
        {
            newHex = string.Empty;
            var initial = ToDrawingColor(currentHex) ?? System.Drawing.Color.LightGray;
            using var dlg = new System.Windows.Forms.ColorDialog { Color = initial, FullOpen = true, AnyColor = true };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return false;
            newHex = ToHex(dlg.Color);
            return true;
        }

        /// <summary>Abre a paleta a partir do texto de um TextBox e grava o hex escolhido de volta nele.</summary>
        public static bool PickInto(TextBox box)
        {
            if (box == null) return false;
            if (!TryPick(box.Text, out var hex)) return false;
            box.Text = hex;
            return true;
        }

        /// <summary>Hex (#RRGGBB, com/sem #) → cor do WPF. null se inválido.</summary>
        public static Color? ToMediaColor(string? hex)
        {
            var c = ToDrawingColor(hex);
            return c is { } d ? Color.FromRgb(d.R, d.G, d.B) : (Color?)null;
        }

        /// <summary>Pincel a partir do hex; usa <paramref name="fallback"/> quando o hex é inválido.</summary>
        public static SolidColorBrush BrushFrom(string? hex, Color fallback)
            => new(ToMediaColor(hex) ?? fallback);

        public static string ToHex(System.Drawing.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        private static System.Drawing.Color? ToDrawingColor(string? hex)
        {
            var h = (hex ?? string.Empty).Trim().TrimStart('#');
            if (h.Length == 6
                && byte.TryParse(h[0..2], NumberStyles.HexNumber, null, out var r)
                && byte.TryParse(h[2..4], NumberStyles.HexNumber, null, out var g)
                && byte.TryParse(h[4..6], NumberStyles.HexNumber, null, out var b))
                return System.Drawing.Color.FromArgb(r, g, b);
            return null;
        }
    }
}
