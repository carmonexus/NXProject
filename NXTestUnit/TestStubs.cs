// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using NXProject.Models;
using NXProject.ViewModels;

namespace NXProject.Services;

internal static class PrintService
{
    public static bool PrintProject(Project project, IEnumerable<TaskViewModel> tasks, bool pdfMode) => true;
}
