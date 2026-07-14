using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using ClosedXML.Excel;

namespace NXProject.Community.Services
{
    /// <summary>
    /// Lê/grava um plano de tarefas em .xlsx nativo (ClosedXML), preservando o
    /// restante da planilha (resumo/fórmulas/formatação). Base para a tela Task Plan.
    /// Modelo genérico por enquanto: detecta a linha de cabeçalho e a tabela abaixo.
    /// </summary>
    public sealed class TaskPlanData
    {
        public DataTable Table { get; init; } = new();
        public string SheetName { get; init; } = string.Empty;
        public int HeaderRow { get; set; }
        /// <summary>Coluna da planilha (1-based) por nome de coluna da tabela. Colunas novas
        /// (criadas na tela) não têm entrada e são gravadas após a última coluna usada.</summary>
        public Dictionary<string, int> ColumnSheetMap { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Colunas da planilha excluídas na tela (limpa cabeçalho e dados ao salvar).</summary>
        public List<int> RemovedSheetColumns { get; init; } = new();
        /// <summary>Colunas criadas na tela: ficam no FIM da aba, com o prefixo "xx#_" no
        /// cabeçalho guardando a posição da visão (restaurada ao abrir).</summary>
        public HashSet<string> AppendedColumns { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Colunas com nome fixo (vinculadas ao cronograma): nunca recebem o
        /// prefixo "xx#_" — a posição delas é a física da planilha (muda só via Excel).</summary>
        public HashSet<string> FixedNameColumns { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public static class ExcelTaskPlanService
    {
        public const string FileFilter = "Planilha do Excel (*.xlsx)|*.xlsx|Todos os arquivos (*.*)|*.*";

        /// <summary>Prefixo das colunas auxiliares que guardam a cor de fundo (hex #RRGGBB) das células.</summary>
        public const string ColorColPrefix = "__c_";

        /// <summary>Lê a planilha (mesmo com o Excel aberto — FileShare.ReadWrite).</summary>
        public static TaskPlanData Load(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var wb = new XLWorkbook(fs);
            var ws = wb.Worksheets.First();

            int lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
            int lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
            if (lastRow == 0 || lastCol == 0)
                return new TaskPlanData { SheetName = ws.Name };

            // Reconhece a linha de títulos (não é fixa): pontua cada linha das primeiras 25
            // pela quantidade de células preenchidas, preferindo linhas majoritariamente de
            // texto (títulos são rótulos) e que tenham dados logo abaixo (a tabela começa ali).
            int headerRow = 1;
            double bestScore = -1;
            int scan = Math.Min(lastRow, 25);
            for (int r = 1; r <= scan; r++)
            {
                int filled = 0, textCells = 0;
                for (int c = 1; c <= lastCol; c++)
                {
                    var cell = ws.Cell(r, c);
                    if (cell.IsEmpty()) continue;
                    filled++;
                    if (cell.DataType == XLDataType.Text) textCells++;
                }
                if (filled < 2) continue;

                int belowFilled = 0;
                if (r < lastRow)
                    for (int c = 1; c <= lastCol; c++)
                        if (!ws.Cell(r + 1, c).IsEmpty()) belowFilled++;

                // Prioriza densidade; bônus por ser texto e por haver dados na linha seguinte.
                double score = filled + textCells * 0.5 + (belowFilled >= 2 ? 3 : 0);
                if (score > bestScore) { bestScore = score; headerRow = r; }
            }

            var table = new DataTable();
            var colMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var appended = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var desiredOrdinal = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int c = 1; c <= lastCol; c++)
            {
                var h = ws.Cell(headerRow, c).GetString().Trim();
                if (string.IsNullOrEmpty(h)) continue;

                // Coluna criada na tela: cabeçalho "xx#_Nome" (xx = posição na visão).
                int? ordinal = null;
                var m = System.Text.RegularExpressions.Regex.Match(h, @"^(\d+)#_(.+)$");
                if (m.Success)
                {
                    ordinal = int.Parse(m.Groups[1].Value);
                    h = m.Groups[2].Value.Trim();
                }

                var name = h; int i = 2;
                while (colMap.ContainsKey(name)) name = $"{h} ({i++})";
                table.Columns.Add(name, typeof(string));
                colMap[name] = c;
                if (ordinal.HasValue)
                {
                    appended.Add(name);
                    desiredOrdinal[name] = ordinal.Value;
                }
            }

            // Colunas de dados (as de cor "__c_*" são criadas sob demanda abaixo).
            var dataCols = table.Columns.Cast<DataColumn>().ToList();

            for (int r = headerRow + 1; r <= lastRow; r++)
            {
                if (!colMap.Values.Any(c => !ws.Cell(r, c).IsEmpty())) continue; // linha vazia
                var dr = table.NewRow();
                foreach (var col in dataCols)
                {
                    var cell = ws.Cell(r, colMap[col.ColumnName]);
                    dr[col] = cell.GetString();

                    // Cor de fundo da célula (preenchimento sólido) → coluna auxiliar "__c_*".
                    var hex = GetFillHex(wb, cell);
                    if (hex != null)
                    {
                        var cc = ColorColPrefix + col.ColumnName;
                        if (!table.Columns.Contains(cc)) table.Columns.Add(cc, typeof(string));
                        dr[cc] = hex;
                    }
                }
                table.Rows.Add(dr);
            }

            // Restaura a posição da visão das colunas criadas na tela.
            foreach (var kv in desiredOrdinal.OrderBy(kv => kv.Value))
                table.Columns[kv.Key]!.SetOrdinal(Math.Min(Math.Max(kv.Value - 1, 0), table.Columns.Count - 1));

            var data = new TaskPlanData
            {
                Table = table,
                SheetName = ws.Name,
                HeaderRow = headerRow
            };
            foreach (var kv in colMap) data.ColumnSheetMap[kv.Key] = kv.Value;
            foreach (var a in appended) data.AppendedColumns.Add(a);
            return data;
        }

        /// <summary>
        /// Grava as linhas de volta na mesma região da tabela, preservando o resto da
        /// planilha (resumo/fórmulas). Requer o arquivo NÃO estar aberto no Excel
        /// (lança IOException se travado — o chamador avisa).
        /// </summary>
        public static void Save(string path, TaskPlanData data)
        {
            using var wb = new XLWorkbook(path);
            var ws = wb.Worksheet(data.SheetName);
            int headerRow = data.HeaderRow;
            int lastUsedRow = ws.LastRowUsed()?.RowNumber() ?? headerRow;

            // Colunas visíveis da tabela (ignora as auxiliares __*).
            var cols = data.Table.Columns.Cast<DataColumn>()
                .Where(c => !c.ColumnName.StartsWith("__", StringComparison.Ordinal))
                .ToList();

            // Colunas excluídas na tela: limpa cabeçalho e dados na planilha.
            foreach (var c in data.RemovedSheetColumns)
                for (int r = headerRow; r <= lastUsedRow; r++)
                    ws.Cell(r, c).Clear(XLClearOptions.Contents);

            // Colunas novas (sem posição na planilha) entram após a última coluna usada,
            // preservando o restante da aba (resumo/fórmulas das colunas originais).
            int maxCol = Math.Max(ws.LastColumnUsed()?.ColumnNumber() ?? 0,
                data.ColumnSheetMap.Values.DefaultIfEmpty(0).Max());
            foreach (var col in cols)
                if (!data.ColumnSheetMap.ContainsKey(col.ColumnName))
                {
                    data.ColumnSheetMap[col.ColumnName] = ++maxCol;
                    data.AppendedColumns.Add(col.ColumnName);
                }

            // Cabeçalhos (cobre colunas renomeadas e novas). Colunas criadas na tela, ou
            // movidas para fora da posição física da planilha, levam o prefixo "xx#_" com a
            // posição da visão (restaurada na próxima abertura). Colunas vinculadas ao
            // cronograma nunca são prefixadas: a posição delas é a física (muda só no Excel).
            var sheetOrder = cols.OrderBy(c => data.ColumnSheetMap[c.ColumnName]).ToList();
            foreach (var col in cols)
            {
                var name = col.ColumnName;
                bool moved = sheetOrder.IndexOf(col) != cols.IndexOf(col);
                bool prefix = !data.FixedNameColumns.Contains(name)
                              && (data.AppendedColumns.Contains(name) || moved);
                ws.Cell(headerRow, data.ColumnSheetMap[name]).Value =
                    prefix ? $"{cols.IndexOf(col) + 1}#_{name}" : name;
            }

            // Limpa o conteúdo antigo das colunas da tabela (da 1ª linha de dados até o fim);
            // nas colunas com controle de cor, limpa também o preenchimento.
            for (int r = headerRow + 1; r <= lastUsedRow; r++)
                foreach (var col in cols)
                {
                    var cell = ws.Cell(r, data.ColumnSheetMap[col.ColumnName]);
                    cell.Clear(XLClearOptions.Contents);
                    if (data.Table.Columns.Contains(ColorColPrefix + col.ColumnName))
                        cell.Style.Fill.BackgroundColor = XLColor.NoColor;
                }

            // Escreve as linhas atuais (valor + cor de fundo, quando controlada).
            int rr = headerRow + 1;
            foreach (DataRow dr in data.Table.Rows)
            {
                foreach (var col in cols)
                {
                    var val = dr[col]?.ToString() ?? string.Empty;
                    var cell = ws.Cell(rr, data.ColumnSheetMap[col.ColumnName]);
                    // Preserva números como número (senão fórmulas de contagem quebram).
                    if (double.TryParse(val, NumberStyles.Any, CultureInfo.CurrentCulture, out var num)
                        || double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out num))
                        cell.Value = num;
                    else
                        cell.Value = val;

                    var cc = ColorColPrefix + col.ColumnName;
                    if (data.Table.Columns.Contains(cc))
                    {
                        var hex = dr[cc]?.ToString();
                        if (!string.IsNullOrWhiteSpace(hex))
                        {
                            try { cell.Style.Fill.BackgroundColor = XLColor.FromHtml(hex); }
                            catch { /* hex inválido: ignora */ }
                        }
                    }
                }
                rr++;
            }

            wb.Save();
        }

        /// <summary>
        /// Cor de fundo da célula em "#RRGGBB", resolvendo também as cores de TEMA do Excel
        /// (paleta padrão do botão de preenchimento, com tonalidade). Null se sem preenchimento.
        /// </summary>
        private static string? GetFillHex(XLWorkbook wb, IXLCell cell)
        {
            var fill = cell.Style.Fill;
            if (fill.PatternType != XLFillPatternValues.Solid || fill.BackgroundColor == null)
                return null;

            try
            {
                var bg = fill.BackgroundColor;
                System.Drawing.Color c;
                if (bg.ColorType == XLColorType.Theme)
                {
                    c = wb.Theme.ResolveThemeColor(bg.ThemeColor).Color;
                    // Aplica a tonalidade (tint): >0 clareia em direção ao branco; <0 escurece.
                    var tint = bg.ThemeTint;
                    double Apply(double ch) => tint >= 0 ? ch + (255 - ch) * tint : ch * (1 + tint);
                    c = System.Drawing.Color.FromArgb(
                        (int)Math.Round(Math.Clamp(Apply(c.R), 0, 255)),
                        (int)Math.Round(Math.Clamp(Apply(c.G), 0, 255)),
                        (int)Math.Round(Math.Clamp(Apply(c.B), 0, 255)));
                }
                else
                {
                    c = bg.Color;   // RGB direto e indexado
                }
                return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            }
            catch { return null; }
        }

        /// <summary>Cria um .xlsx novo com o cabeçalho na linha 1 e as linhas da tabela.</summary>
        public static void CreateNew(string path, TaskPlanData data)
        {
            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet(string.IsNullOrWhiteSpace(data.SheetName) ? "Tarefas" : data.SheetName);

            // Ignora colunas auxiliares (ex.: __m_* de validação).
            var cols = data.Table.Columns.Cast<DataColumn>()
                .Where(c => !c.ColumnName.StartsWith("__", StringComparison.Ordinal))
                .ToList();

            for (int c = 0; c < cols.Count; c++)
            {
                var cell = ws.Cell(1, c + 1);
                cell.Value = cols[c].ColumnName;
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromArgb(0x2B, 0x57, 0x9A);
                cell.Style.Font.FontColor = XLColor.White;
            }

            int rr = 2;
            foreach (DataRow dr in data.Table.Rows)
            {
                for (int c = 0; c < cols.Count; c++)
                {
                    var val = dr[cols[c]]?.ToString() ?? string.Empty;
                    var cell = ws.Cell(rr, c + 1);
                    if (double.TryParse(val, NumberStyles.Any, CultureInfo.CurrentCulture, out var num)
                        || double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out num))
                        cell.Value = num;
                    else
                        cell.Value = val;
                }
                rr++;
            }

            ws.Columns().AdjustToContents(1, Math.Max(2, rr - 1));
            wb.SaveAs(path);
        }

        /// <summary>True se o arquivo estiver travado (aberto no Excel) para escrita.</summary>
        public static bool IsLockedForWrite(string path)
        {
            try
            {
                using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (IOException) { return true; }
        }
    }
}
