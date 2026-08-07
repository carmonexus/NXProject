# DLLs necessarias no NXProject-Setup.zip

Esta lista protege dependencias gerenciadas que precisam acompanhar o pacote de base.
Quando uma dependencia NuGet nova for adicionada ao app, inclua aqui as DLLs que o
`NXProject-Setup.zip` deve distribuir.

O `llama.dll` nativo nao entra nesta lista: ele e baixado pelo gerenciador de IA Local.
DLLs do runtime .NET/WPF tambem nao entram aqui; elas ja sao validadas pelo manifesto
do Setup.

## Arquivos obrigatorios

- ClosedXML.dll
- ClosedXML.Parser.dll
- CommunityToolkit.HighPerformance.dll
- CommunityToolkit.Mvvm.dll
- DocumentFormat.OpenXml.dll
- DocumentFormat.OpenXml.Framework.dll
- ExcelNumberFormat.dll
- LLamaSharp.dll
- Microsoft.Bcl.AsyncInterfaces.dll
- Microsoft.Bcl.Memory.dll
- Microsoft.Extensions.AI.Abstractions.dll
- Microsoft.Extensions.DependencyInjection.Abstractions.dll
- Microsoft.Extensions.Logging.Abstractions.dll
- Microsoft.Web.WebView2.Core.dll
- Microsoft.Web.WebView2.WinForms.dll
- Microsoft.Web.WebView2.Wpf.dll
- PdfSharp.BarCodes.dll
- PdfSharp.Charting.dll
- PdfSharp.Cryptography.dll
- PdfSharp.dll
- PdfSharp.Quality.dll
- PdfSharp.Shared.dll
- PdfSharp.Snippets.dll
- PdfSharp.System.dll
- PdfSharp.WPFonts.dll
- RBush.dll
- SixLabors.Fonts.dll
- System.Interactive.Async.dll
- System.Linq.Async.dll
- System.Numerics.Tensors.dll
- WebView2Loader.dll
