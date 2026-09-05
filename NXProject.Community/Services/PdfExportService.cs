// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace NXProject.Community.Services
{
    // Resolve fontes do Windows para o PDFsharp 6.x (que não usa GDI+ por padrão)
    internal sealed class WindowsFontResolver : IFontResolver
    {
        private static readonly string FontsFolder =
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

        // Mapeamento família → arquivo(s) de fonte no Windows
        private static readonly Dictionary<string, (string regular, string bold)> Map =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Segoe UI"]  = ("segoeui",  "segoeuib"),
                ["Arial"]     = ("arial",    "arialbd"),
                ["Helvetica"] = ("arial",    "arialbd"),
                ["Calibri"]   = ("calibri",  "calibrib"),
            };

        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            if (!Map.TryGetValue(familyName, out var files))
                files = ("arial", "arialbd"); // fallback seguro

            var face = isBold ? files.bold : files.regular;
            return new FontResolverInfo(face);
        }

        public byte[]? GetFont(string faceName)
        {
            var path = Path.Combine(FontsFolder, faceName + ".ttf");
            if (File.Exists(path)) return File.ReadAllBytes(path);

            // Tenta variantes comuns
            path = Path.Combine(FontsFolder, faceName + ".otf");
            if (File.Exists(path)) return File.ReadAllBytes(path);

            // Fallback final: Arial regular
            path = Path.Combine(FontsFolder, "arial.ttf");
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
    }

    internal static class PdfExportService
    {
        public sealed record PdfGanttData(
            IReadOnlyList<PdfGanttTask> Tasks,
            IReadOnlyList<PdfGanttSprint> Sprints,
            DateTime Start,
            int VisibleDays,
            double DayWidth,
            double HeaderHeight,
            double RowHeight);

        public sealed record PdfGanttTask(
            string Id,
            string TypeTag,
            string Name,
            int Depth,
            double DurationHours,
            double Sfp,
            DateTime Start,
            DateTime Finish,
            string FinishText,
            bool IsSummary,
            bool IsMilestone,
            double PercentComplete,
            string Predecessors,
            string Resources,
            string Sprint);

        public sealed record PdfGanttSprint(
            string Name,
            int Number,
            DateTime Start,
            DateTime End);

        private static bool _fontResolverRegistered;

        private static void EnsureFontResolver()
        {
            if (_fontResolverRegistered) return;
            GlobalFontSettings.FontResolver = new WindowsFontResolver();
            _fontResolverRegistered = true;
        }
        // Margens e dimensões em pontos PDF (1 pt = 1/72 pol)
        private const double Margin     = 20;
        private const double HeaderH    = 58; // altura da faixa de cabeçalho
        private const double FooterH    = 28;
        private const double SepLine    = 0.8;
        private const double SepGap     = 4;
        private const double PdfRenderDpi = 300;
        private const int MaxRenderedPixels = 80_000_000;

        /// <summary>
        /// Exporta o cronograma para PDF.
        /// Modo "Together": tabela + Gantt juntos no tamanho indicado por <paramref name="pageSize"/>.
        /// Modo "TwoPages": página 1 = tableVisual, página 2 = ganttVisual.
        /// </summary>
        public static void Export(
            FrameworkElement tableVisual,
            FrameworkElement ganttVisual,
            PdfGanttData? ganttData,
            string projectName,
            string companyName,
            BitmapImage? companyLogo,
            string filePath,
            NXProject.Views.PdfLayoutMode layoutMode     = NXProject.Views.PdfLayoutMode.Together,
            PdfSharp.PageSize   pageSize       = PdfSharp.PageSize.A3,
            string exportedOnLabel = "Exportado em",
            string scheduleSubject = "Cronograma NXProject")
        {
            EnsureFontResolver();

            var doc = new PdfDocument();
            doc.Info.Title   = projectName;
            doc.Info.Subject = scheduleSubject;
            doc.Info.Creator = "NXProject Community";

            if (layoutMode == NXProject.Views.PdfLayoutMode.TwoPages)
            {
                if (ganttData != null)
                {
                    AddSeparateSchedulePage(doc, ganttData, drawGantt: false,
                        PdfSharp.PageSize.A4, PdfSharp.PageOrientation.Landscape,
                        companyName, companyLogo, projectName, exportedOnLabel);

                    AddSeparateSchedulePage(doc, ganttData, drawGantt: true,
                        PdfSharp.PageSize.A4, PdfSharp.PageOrientation.Landscape,
                        companyName, companyLogo, projectName, exportedOnLabel);
                }
                else
                {
                    AddContentPage(doc, tableVisual,
                        PdfSharp.PageSize.A4, PdfSharp.PageOrientation.Landscape,
                        companyName, companyLogo, projectName, exportedOnLabel, pageNum: 1);

                    AddContentPage(doc, ganttVisual,
                        PdfSharp.PageSize.A4, PdfSharp.PageOrientation.Landscape,
                        companyName, companyLogo, projectName, exportedOnLabel, pageNum: 2);
                }
            }
            else
            {
                // Renderiza tabela e Gantt separadamente e os posiciona lado a lado
                AddSideBySidePage(doc,
                    tableVisual, ganttVisual,
                    ganttData,
                    pageSize, companyName, companyLogo, projectName, exportedOnLabel);
            }

            doc.Save(filePath);
        }

        private static void AddSideBySidePage(
            PdfDocument doc,
            FrameworkElement tableVisual, FrameworkElement ganttVisual,
            PdfGanttData? ganttData,
            PdfSharp.PageSize size,
            string companyName, BitmapImage? companyLogo,
            string projectName, string exportedOnLabel)
        {
            var page = doc.AddPage();
            page.Size        = size;
            page.Orientation = PdfSharp.PageOrientation.Landscape;

            using var gfx = XGraphics.FromPdfPage(page);
            double pageW = page.Width.Point;
            double pageH = page.Height.Point;

            bool hasHeader = companyLogo != null || !string.IsNullOrWhiteSpace(companyName);
            double contentTop = Margin;
            if (hasHeader)
            {
                DrawHeader(gfx, pageW, companyName, companyLogo, projectName);
                contentTop = Margin + HeaderH + SepLine + SepGap;
            }

            double footerTop = pageH - Margin - FooterH;
            double contentH  = footerTop - SepGap - SepLine - contentTop;
            double availW    = pageW - Margin * 2;

            double sepW = 4;
            // Gantt width: proportional to visible days (natural scale capped at 55% of page).
            // Avoids over-stretching when only a short date range is visible.
            double ganttW;
            if (ganttData != null)
            {
                var naturalGanttSourceW = 16.0 + Math.Max(1, ganttData.VisibleDays) * ganttData.DayWidth;
                // The natural gantt width in page-points: scale so that ganttData.DayWidth px = a reasonable pt width.
                // We use the "fit to 55%" scale as the ceiling and the natural proportion as the floor.
                double maxGanttW = Math.Round(availW * 0.55);
                double minGanttW = Math.Round(availW * 0.30);
                // Proportion of content vs a reference of 90 visible days (full-project typical)
                const double ReferenceDays = 90.0;
                double naturalRatio = Math.Min(1.0, ganttData.VisibleDays / ReferenceDays);
                ganttW = Math.Max(minGanttW, Math.Min(maxGanttW, Math.Round(availW * 0.55 * naturalRatio)));
            }
            else
            {
                ganttW = Math.Round(availW * 0.55);
            }
            double tableW = availW - ganttW - sepW;

            if (ganttData != null)
            {
                DrawScheduleTable(gfx, new XRect(Margin, contentTop, tableW, contentH), ganttData, compactForCombined: true);
            }
            else
            {
                var tablePng = RenderToPng(tableVisual, PdfRenderDpi);
                using (var ms = new MemoryStream(tablePng))
                using (var img = XImage.FromStream(ms))
                    gfx.DrawImage(img, Margin, contentTop, tableW, contentH);
            }

            // Linha separadora vertical
            var vpen = new XPen(XColor.FromArgb(200, 200, 200), 0.5);
            gfx.DrawLine(vpen, Margin + tableW + sepW / 2, contentTop,
                               Margin + tableW + sepW / 2, contentTop + contentH);

            var ganttRect = new XRect(Margin + tableW + sepW, contentTop, ganttW, contentH);
            if (ganttData != null)
            {
                DrawGantt(gfx, ganttRect, ganttData);
            }
            else
            {
                var ganttPng = RenderToPng(ganttVisual, PdfRenderDpi);
                using (var ms = new MemoryStream(ganttPng))
                using (var img = XImage.FromStream(ms))
                    gfx.DrawImage(img, ganttRect);
            }

            DrawFooter(gfx, pageW, pageH, projectName, exportedOnLabel,
                GetPageFormatLabel(size, PdfSharp.PageOrientation.Landscape), companyName, companyLogo);
        }

        private static void AddSeparateSchedulePage(
            PdfDocument doc,
            PdfGanttData ganttData,
            bool drawGantt,
            PdfSharp.PageSize size,
            PdfSharp.PageOrientation orientation,
            string companyName,
            BitmapImage? companyLogo,
            string projectName,
            string exportedOnLabel)
        {
            var page = doc.AddPage();
            page.Size = size;
            page.Orientation = orientation;

            using var gfx = XGraphics.FromPdfPage(page);
            double pageW = page.Width.Point;
            double pageH = page.Height.Point;

            bool hasHeader = companyLogo != null || !string.IsNullOrWhiteSpace(companyName);
            double contentTop = Margin;
            if (hasHeader)
            {
                DrawHeader(gfx, pageW, companyName, companyLogo, projectName);
                contentTop = Margin + HeaderH + SepLine + SepGap;
            }

            double footerTop = pageH - Margin - FooterH;
            double contentH = footerTop - SepGap - SepLine - contentTop;
            double contentW = pageW - Margin * 2;
            var contentRect = new XRect(Margin, contentTop, contentW, contentH);

            if (drawGantt)
                DrawGantt(gfx, contentRect, ganttData);
            else
                DrawScheduleTable(gfx, contentRect, ganttData, compactForCombined: false);

            DrawFooter(gfx, pageW, pageH, projectName, exportedOnLabel,
                GetPageFormatLabel(size, orientation), companyName, companyLogo);
        }

        private static void DrawScheduleTable(XGraphics gfx, XRect rect, PdfGanttData data, bool compactForCombined)
        {
            var sourceH = data.HeaderHeight + Math.Max(1, data.Tasks.Count) * data.RowHeight + 4;
            var scaleY = rect.Height / sourceH;
            var headerH = data.HeaderHeight * scaleY;
            var rowH = data.RowHeight * scaleY;
            var headerFont = new XFont("Segoe UI", 5.2, XFontStyleEx.Bold);
            var cellFont = new XFont("Segoe UI", 4.9, XFontStyleEx.Regular);
            var boldFont = new XFont("Segoe UI", 4.9, XFontStyleEx.Bold);
            var headerBrush = new XSolidBrush(XColor.FromArgb(232, 232, 232));
            var borderPen = new XPen(XColor.FromArgb(210, 210, 210), 0.3);
            var textBrush = new XSolidBrush(XColor.FromArgb(35, 35, 35));
            var blueBrush = new XSolidBrush(XColor.FromArgb(43, 87, 154));

            gfx.DrawRectangle(XBrushes.White, rect);
            gfx.DrawRectangle(headerBrush, rect.X, rect.Y, rect.Width, headerH);

            var columns = compactForCombined
                ? GetCombinedTableColumns()
                : GetSeparateTableColumns();

            var totalWeight = columns.Sum(c => c.Weight);
            var x = rect.X;
            var colRects = new XRect[columns.Length];
            for (int i = 0; i < columns.Length; i++)
            {
                var w = rect.Width * columns[i].Weight / totalWeight;
                colRects[i] = new XRect(x, rect.Y, w, rect.Height);
                gfx.DrawLine(borderPen, x, rect.Y, x, rect.Bottom);
                gfx.DrawString(columns[i].Header, headerFont, textBrush,
                    new XRect(x + 1, rect.Y + 1, Math.Max(1, w - 2), headerH - 2),
                    XStringFormats.CenterLeft);
                x += w;
            }
            gfx.DrawLine(borderPen, rect.Right, rect.Y, rect.Right, rect.Bottom);

            for (int row = 0; row < data.Tasks.Count; row++)
            {
                var task = data.Tasks[row];
                var y = rect.Y + headerH + row * rowH;
                gfx.DrawLine(borderPen, rect.X, y, rect.Right, y);

                for (int col = 0; col < columns.Length; col++)
                {
                    var cr = colRects[col];
                    var cell = new XRect(cr.X + 1, y + 0.5, Math.Max(1, cr.Width - 2), Math.Max(1, rowH - 1));

                    if (columns[col].Header == "% Compl.")
                    {
                        DrawPercentCell(gfx, cell, task.PercentComplete);
                        continue;
                    }

                    var value = columns[col].Text(task);
                    var font = task.IsSummary || (columns[col].Header == "Nome da Tarefa" && task.Depth == 0)
                        ? boldFont
                        : cellFont;
                    var brush = columns[col].Header == "ID" ? blueBrush : textBrush;
                    var textX = columns[col].Header == "Nome da Tarefa"
                        ? cell.X + Math.Min(10, task.Depth * 2.8)
                        : cell.X;
                    var textW = Math.Max(1, cell.Right - textX);
                    gfx.DrawString(TrimForCell(value, textW), font, brush,
                        new XRect(textX, cell.Y, textW, cell.Height), XStringFormats.CenterLeft);
                }
            }

            gfx.DrawLine(borderPen, rect.X, rect.Bottom, rect.Right, rect.Bottom);
        }

        private static (string Header, double Weight, Func<PdfGanttTask, string> Text)[] GetCombinedTableColumns()
        {
            return new (string Header, double Weight, Func<PdfGanttTask, string> Text)[]
            {
                ("ID", 0.048, t => t.Id),
                ("Tipo / Estado", 0.078, t => t.TypeTag),
                ("Nome da Tarefa", 0.370, t => t.Name),
                ("Dur.(h)", 0.038, t => t.DurationHours.ToString("0.#")),
                ("SFP", 0.026, t => t.Sfp.ToString("0")),
                ("Inicio", 0.050, t => t.Start.ToString("dd/MM/yy")),
                ("Fim", 0.050, t => t.FinishText),
                ("% Compl.", 0.040, t => string.Empty),
                ("Pred.", 0.036, t => t.Predecessors),
                ("Recursos", 0.160, t => t.Resources),
                ("Sprint", 0.092, t => t.Sprint)
            };
        }

        private static (string Header, double Weight, Func<PdfGanttTask, string> Text)[] GetSeparateTableColumns()
        {
            return new (string Header, double Weight, Func<PdfGanttTask, string> Text)[]
            {
                ("ID", 0.040, t => t.Id),
                ("Tipo / Estado", 0.075, t => t.TypeTag),
                ("Nome da Tarefa", 0.420, t => t.Name),
                ("Dur.(h)", 0.034, t => t.DurationHours.ToString("0.#")),
                ("SFP", 0.024, t => t.Sfp.ToString("0")),
                ("Inicio", 0.044, t => t.Start.ToString("dd/MM/yy")),
                ("Fim", 0.044, t => t.FinishText),
                ("% Compl.", 0.034, t => string.Empty),
                ("Pred.", 0.032, t => t.Predecessors),
                ("Recursos", 0.168, t => t.Resources),
                ("Sprint", 0.085, t => t.Sprint)
            };
        }

        private static void DrawPercentCell(XGraphics gfx, XRect cell, double percent)
        {
            var barW = Math.Max(8, cell.Width - 2);
            var barH = Math.Max(2, cell.Height * 0.45);
            var x = cell.X + 1;
            var y = cell.Y + (cell.Height - barH) / 2;
            gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(225, 225, 225)), x, y, barW, barH);
            if (percent > 0)
                gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(33, 115, 70)), x, y, barW * Math.Min(100, percent) / 100.0, barH);
            gfx.DrawString($"{percent:0}%", new XFont("Segoe UI", 4.1, XFontStyleEx.Regular),
                new XSolidBrush(XColor.FromArgb(45, 45, 45)), cell, XStringFormats.Center);
        }

        private static string TrimForCell(string? text, double width)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var maxChars = Math.Max(3, (int)(width / 2.6));
            return text.Length <= maxChars ? text : text[..Math.Max(1, maxChars - 1)] + "…";
        }

        private static void DrawGantt(XGraphics gfx, XRect rect, PdfGanttData data)
        {
            var sourceW = 16.0 + Math.Max(1, data.VisibleDays) * data.DayWidth;
            var sourceH = data.HeaderHeight + Math.Max(1, data.Tasks.Count) * data.RowHeight + 4;
            // Scale down if content is wider than rect, but never stretch (cap at 1.0)
            var scaleX = Math.Min(1.0, rect.Width / sourceW);
            var scaleY = rect.Height / sourceH;
            var leftPad = 16.0 * scaleX;
            var dayW = data.DayWidth * scaleX;
            var headerH = data.HeaderHeight * scaleY;
            var rowH = data.RowHeight * scaleY;

            gfx.DrawRectangle(XBrushes.White, rect);
            DrawGanttHeader(gfx, rect, data, leftPad, dayW, headerH);
            DrawGanttBody(gfx, rect, data, leftPad, dayW, headerH, rowH);
        }

        private static void DrawGanttHeader(
            XGraphics gfx,
            XRect rect,
            PdfGanttData data,
            double leftPad,
            double dayW,
            double headerH)
        {
            var monthH = Math.Max(10, headerH * 0.48);
            var sprintTop = rect.Top + monthH;
            var sprintH = Math.Max(8, headerH - monthH);
            var linePen = new XPen(XColor.FromArgb(190, 200, 215), 0.35);
            var monthFont = new XFont("Segoe UI", 6.2, XFontStyleEx.Regular);
            var sprintFont = new XFont("Segoe UI", 5.8, XFontStyleEx.Bold);

            gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(232, 232, 232)), rect.X, rect.Y, rect.Width, monthH);
            gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(220, 228, 240)), rect.X, sprintTop, rect.Width, sprintH);

            var cursor = new DateTime(data.Start.Year, data.Start.Month, 1);
            if (cursor > data.Start)
                cursor = cursor.AddMonths(-1);

            var end = data.Start.AddDays(data.VisibleDays);
            while (cursor < end)
            {
                var next = cursor.AddMonths(1);
                var startOffset = Math.Max(0, (cursor - data.Start).TotalDays);
                var endOffset = Math.Min(data.VisibleDays, (next - data.Start).TotalDays);
                var x1 = rect.X + leftPad + startOffset * dayW;
                var x2 = rect.X + leftPad + endOffset * dayW;

                if (x2 > rect.X && x1 < rect.Right)
                {
                    gfx.DrawLine(linePen, x1, rect.Y, x1, rect.Bottom);
                    gfx.DrawString(
                        cursor.ToString("MMM/yy"),
                        monthFont,
                        new XSolidBrush(XColor.FromArgb(95, 105, 120)),
                        new XRect(x1, rect.Y + 1, Math.Max(18, x2 - x1), monthH - 2),
                        XStringFormats.Center);
                }

                cursor = next;
            }

            foreach (var sprint in data.Sprints)
            {
                var startOffset = (sprint.Start.Date - data.Start).TotalDays;
                var endOffset = (sprint.End.Date - data.Start).TotalDays + 1;
                if (endOffset < 0 || startOffset > data.VisibleDays)
                    continue;

                var x = rect.X + leftPad + Math.Max(0, startOffset) * dayW;
                var w = Math.Max(6, (Math.Min(data.VisibleDays, endOffset) - Math.Max(0, startOffset)) * dayW);
                var fill = sprint.Number % 2 == 0
                    ? XColor.FromArgb(210, 221, 236)
                    : XColor.FromArgb(222, 231, 243);

                gfx.DrawRectangle(new XPen(XColor.FromArgb(180, 194, 214), 0.35), new XSolidBrush(fill), x, sprintTop, w, sprintH);
                gfx.DrawString(
                    sprint.Name,
                    sprintFont,
                    new XSolidBrush(XColor.FromArgb(43, 87, 154)),
                    new XRect(x + 1, sprintTop + 1, Math.Max(1, w - 2), sprintH - 2),
                    XStringFormats.Center);
            }

            gfx.DrawLine(new XPen(XColors.LightGray, 0.5), rect.X, rect.Top + headerH - 0.5, rect.Right, rect.Top + headerH - 0.5);
        }

        private static void DrawGanttBody(
            XGraphics gfx,
            XRect rect,
            PdfGanttData data,
            double leftPad,
            double dayW,
            double headerH,
            double rowH)
        {
            var bodyTop = rect.Top + headerH;
            var bodyH = rect.Height - headerH;
            var gridPen = new XPen(XColor.FromArgb(235, 235, 235), 0.25);
            var majorPen = new XPen(XColor.FromArgb(220, 225, 232), 0.35);

            for (int i = 0; i <= data.Tasks.Count; i++)
            {
                var y = bodyTop + i * rowH;
                gfx.DrawLine(gridPen, rect.X, y, rect.Right, y);
            }

            var cursor = new DateTime(data.Start.Year, data.Start.Month, 1);
            if (cursor > data.Start)
                cursor = cursor.AddMonths(-1);
            var end = data.Start.AddDays(data.VisibleDays);
            while (cursor <= end)
            {
                var x = rect.X + leftPad + (cursor - data.Start).TotalDays * dayW;
                if (x >= rect.X && x <= rect.Right)
                    gfx.DrawLine(majorPen, x, bodyTop, x, rect.Bottom);
                cursor = cursor.AddMonths(1);
            }

            var todayOffset = (DateTime.Today.Date - data.Start).TotalDays;
            if (todayOffset >= 0 && todayOffset <= data.VisibleDays)
            {
                var todayX = rect.X + leftPad + todayOffset * dayW;
                gfx.DrawLine(new XPen(XColor.FromArgb(255, 69, 0), 0.75) { DashStyle = XDashStyle.Dash },
                    todayX, bodyTop, todayX, rect.Bottom);
            }

            for (int i = 0; i < data.Tasks.Count; i++)
            {
                var task = data.Tasks[i];
                var y = bodyTop + i * rowH;
                var startOffset = (task.Start.Date - data.Start).TotalDays;
                var endOffset = (task.Finish.Date - data.Start).TotalDays;
                var x = rect.X + leftPad + startOffset * dayW;
                var w = Math.Max(0.8, (endOffset - startOffset) * dayW);

                if (x + w < rect.X || x > rect.Right)
                    continue;

                x = Math.Max(rect.X, x);
                w = Math.Min(rect.Right - x, w);

                if (task.IsMilestone)
                    DrawGanttMilestone(gfx, x, y, rowH);
                else
                    DrawGanttBar(gfx, x, y, w, rowH, task);
            }
        }

        private static void DrawGanttBar(XGraphics gfx, double x, double y, double w, double rowH, PdfGanttTask task)
        {
            var pad = Math.Max(1.2, rowH * 0.18);
            var barH = Math.Max(2, rowH - pad * 2 - (task.IsSummary ? 0.8 : 0));
            var color = task.IsSummary ? XColor.FromArgb(43, 87, 154) : XColor.FromArgb(68, 114, 196);
            gfx.DrawRoundedRectangle(new XSolidBrush(color), x, y + pad, w, barH, 1, 1);

            if (!task.IsSummary && task.PercentComplete > 0)
            {
                var pw = w * Math.Min(100, task.PercentComplete) / 100.0;
                gfx.DrawRoundedRectangle(new XSolidBrush(XColor.FromArgb(33, 115, 70)), x, y + pad, pw, barH, 1, 1);
            }

            var dotSize = Math.Max(1.8, Math.Min(3.2, rowH * 0.23));
            gfx.DrawEllipse(new XSolidBrush(XColor.FromArgb(100, 100, 100)), x - dotSize / 2, y + rowH / 2 - dotSize / 2, dotSize, dotSize);
        }

        private static void DrawGanttMilestone(XGraphics gfx, double x, double y, double rowH)
        {
            var size = Math.Max(3, rowH * 0.55);
            var yc = y + rowH / 2;
            var points = new[]
            {
                new XPoint(x, yc),
                new XPoint(x + size / 2, yc - size / 2),
                new XPoint(x + size, yc),
                new XPoint(x + size / 2, yc + size / 2)
            };
            gfx.DrawPolygon(new XPen(XColors.DarkGoldenrod, 0.4), XBrushes.Goldenrod, points, XFillMode.Winding);
        }

        private static void AddContentPage(
            PdfDocument doc,
            FrameworkElement visual,
            PdfSharp.PageSize size,
            PdfSharp.PageOrientation orientation,
            string companyName, BitmapImage? companyLogo,
            string projectName,
            string exportedOnLabel, int pageNum = 0)
        {
            var page = doc.AddPage();
            page.Size        = size;
            page.Orientation = orientation;

            using var gfx = XGraphics.FromPdfPage(page);
            double pageW = page.Width.Point;
            double pageH = page.Height.Point;

            bool hasHeader = companyLogo != null || !string.IsNullOrWhiteSpace(companyName);

            double contentTop = Margin;
            if (hasHeader)
            {
                DrawHeader(gfx, pageW, companyName, companyLogo, projectName);
                contentTop = Margin + HeaderH + SepLine + SepGap;
            }

            double footerTop = pageH - Margin - FooterH;
            double imgH = footerTop - SepGap - SepLine - contentTop;
            double imgW = pageW - Margin * 2;

            var pngBytes = RenderToPng(visual, PdfRenderDpi);
            using var ms   = new MemoryStream(pngBytes);
            using var xImg = XImage.FromStream(ms);
            gfx.DrawImage(xImg, Margin, contentTop, imgW, imgH);

            DrawFooter(gfx, pageW, pageH, projectName, exportedOnLabel,
                GetPageFormatLabel(size, orientation), companyName, companyLogo);
        }

        // ── Cabeçalho ─────────────────────────────────────────────────────

        private static void DrawHeader(XGraphics gfx, double pageW,
                                       string companyName, BitmapImage? companyLogo,
                                       string projectName)
        {
            double logoH = HeaderH - 6;
            double curX  = Margin;
            double midY  = Margin + HeaderH / 2.0;

            // Logo da empresa (esquerda)
            if (companyLogo != null)
            {
                try
                {
                    var bytes = BitmapToPng(companyLogo);
                    using var ms  = new MemoryStream(bytes);
                    using var img = XImage.FromStream(ms);
                    double logoW = logoH * img.PixelWidth / (double)img.PixelHeight;
                    gfx.DrawImage(img, curX, Margin + 4, logoW, logoH);
                    curX += logoW + 12;
                }
                catch { /* logo opcional */ }
            }

            // Nome da empresa (se houver)
            if (!string.IsNullOrWhiteSpace(companyName))
            {
                var fComp = new XFont("Segoe UI", 10, XFontStyleEx.Bold);
                gfx.DrawString(companyName, fComp, new XSolidBrush(XColor.FromArgb(30, 30, 30)),
                    new XRect(curX, midY - 14, 200, 16), XStringFormats.CenterLeft);
                curX += 210;
            }

            // Título do projeto (centro-esquerda após logo/empresa)
            if (!string.IsNullOrWhiteSpace(projectName))
            {
                var fTitle = new XFont("Segoe UI", 14, XFontStyleEx.Bold);
                double titleX = curX + 10;
                double titleW = pageW - titleX - Margin;
                gfx.DrawString(projectName, fTitle, new XSolidBrush(XColor.FromArgb(30, 67, 132)),
                    new XRect(titleX, midY - 9, titleW, 20), XStringFormats.CenterLeft);
            }

            // Linha separadora abaixo do cabeçalho
            double lineY = Margin + HeaderH;
            var pen = new XPen(XColor.FromArgb(43, 87, 154), SepLine);
            gfx.DrawLine(pen, Margin, lineY, pageW - Margin, lineY);
        }

        // ── Rodapé ────────────────────────────────────────────────────────

        private static void DrawFooter(XGraphics gfx, double pageW, double pageH,
                                       string projectName, string exportedOnLabel,
                                       string pageFormatLabel,
                                       string companyName = "", BitmapImage? companyLogo = null)
        {
            double lineY = pageH - Margin - FooterH;
            double midY  = lineY + SepLine + 6;

            var linePen = new XPen(XColor.FromArgb(200, 200, 200), SepLine);
            gfx.DrawLine(linePen, Margin, lineY, pageW - Margin, lineY);

            // Nome do projeto (esquerda)
            if (!string.IsNullOrWhiteSpace(projectName))
            {
                var f = new XFont("Segoe UI", 9, XFontStyleEx.Bold);
                gfx.DrawString(projectName, f, XBrushes.DarkSlateGray,
                    new XRect(Margin, midY, pageW / 2.0, 14), XStringFormats.CenterLeft);
            }

            // Data (centro)
            {
                var f     = new XFont("Segoe UI", 8, XFontStyleEx.Regular);
                var label = $"{exportedOnLabel} {DateTime.Now:dd/MM/yyyy HH:mm}  |  Formato: {pageFormatLabel}";
                gfx.DrawString(label, f, new XSolidBrush(XColor.FromArgb(140, 140, 140)),
                    new XRect(0, midY, pageW, 14), XStringFormats.Center);
            }

            // Lado direito: "NXProject Community" (texto discreto)
            double rightX = pageW - Margin;
            var fRight = new XFont("Segoe UI", 8, XFontStyleEx.Regular);
            const string brand = "NXProject Community";
            double txtW = 120;
            gfx.DrawString(brand, fRight, new XSolidBrush(XColor.FromArgb(100, 100, 100)),
                new XRect(rightX - txtW, midY, txtW, 14), XStringFormats.CenterRight);
            rightX -= txtW + 8;

            // Logo da empresa (antes do texto NXProject, lado direito)
            if (companyLogo != null)
            {
                try
                {
                    var bytes = BitmapToPng(companyLogo);
                    using var ms  = new MemoryStream(bytes);
                    using var img = XImage.FromStream(ms);
                    double lh = 14;
                    double lw = lh * img.PixelWidth / (double)img.PixelHeight;
                    gfx.DrawImage(img, rightX - lw, midY, lw, lh);
                }
                catch { /* opcional */ }
            }
            else if (!string.IsNullOrWhiteSpace(companyName))
            {
                var fComp = new XFont("Segoe UI", 8, XFontStyleEx.Bold);
                double compW = 100;
                gfx.DrawString(companyName, fComp, new XSolidBrush(XColor.FromArgb(80, 80, 80)),
                    new XRect(rightX - compW, midY, compW, 14), XStringFormats.CenterRight);
            }
        }

        private static string GetPageFormatLabel(PdfSharp.PageSize size, PdfSharp.PageOrientation orientation)
        {
            var sizeLabel = size switch
            {
                PdfSharp.PageSize.A0 => "A0",
                PdfSharp.PageSize.A1 => "A1",
                PdfSharp.PageSize.A2 => "A2",
                PdfSharp.PageSize.A3 => "A3",
                PdfSharp.PageSize.A4 => "A4",
                _ => size.ToString()
            };

            var orientationLabel = orientation == PdfSharp.PageOrientation.Landscape
                ? "paisagem"
                : "retrato";

            return $"{sizeLabel} {orientationLabel}";
        }

        // ── Utilitários ───────────────────────────────────────────────────

        private static byte[] RenderToPng(FrameworkElement element, double dpi)
        {
            double visualWidth = GetRenderableLength(element.ActualWidth, element.Width);
            double visualHeight = GetRenderableLength(element.ActualHeight, element.Height);

            element.Measure(new Size(visualWidth, visualHeight));
            element.Arrange(new Rect(0, 0, visualWidth, visualHeight));
            element.UpdateLayout();

            visualWidth = GetRenderableLength(element.ActualWidth, visualWidth);
            visualHeight = GetRenderableLength(element.ActualHeight, visualHeight);
            double scale = dpi / 96.0;
            int pixW = (int)Math.Round(visualWidth * scale);
            int pixH = (int)Math.Round(visualHeight * scale);

            if (pixW <= 0 || pixH <= 0)
                throw new InvalidOperationException("O elemento não tem tamanho renderizável.");

            var pixelCount = (long)pixW * pixH;
            if (pixelCount > MaxRenderedPixels)
            {
                var reduce = Math.Sqrt(MaxRenderedPixels / (double)pixelCount);
                pixW = Math.Max(1, (int)Math.Floor(pixW * reduce));
                pixH = Math.Max(1, (int)Math.Floor(pixH * reduce));
                dpi *= reduce;
            }

            double renderWidth = pixW * 96.0 / dpi;
            double renderHeight = pixH * 96.0 / dpi;

            RenderOptions.SetBitmapScalingMode(element, BitmapScalingMode.HighQuality);
            RenderOptions.SetEdgeMode(element, EdgeMode.Unspecified);
            TextOptions.SetTextFormattingMode(element, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(element, TextRenderingMode.Auto);

            var brush = new VisualBrush(element)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, renderWidth, renderHeight)
            };

            var rtb = new RenderTargetBitmap(pixW, pixH, dpi, dpi, PixelFormats.Pbgra32);
            var dv  = new DrawingVisual();
            using (var ctx = dv.RenderOpen())
            {
                ctx.DrawRectangle(Brushes.White, null, new Rect(0, 0, renderWidth, renderHeight));
                ctx.DrawRectangle(brush, null, new Rect(0, 0, renderWidth, renderHeight));
            }
            rtb.Render(dv);

            return EncodePng(rtb);
        }

        private static double GetRenderableLength(double actual, double fallback)
        {
            if (!double.IsNaN(actual) && !double.IsInfinity(actual) && actual > 0)
                return actual;

            if (!double.IsNaN(fallback) && !double.IsInfinity(fallback) && fallback > 0)
                return fallback;

            return 1;
        }

        private static byte[] BitmapToPng(BitmapImage src)
        {
            return EncodePng(src);
        }

        private static byte[] EncodePng(BitmapSource src)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
    }
}
