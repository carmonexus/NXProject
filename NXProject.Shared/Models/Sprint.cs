// Copyright (c) Nexus XData Tecnologia Ltda — Todos os direitos reservados.
// NXProject — licenciado sob a NXProject License 2.0 (Open Core / licenciamento dual).
// Licença: LICENSE.txt (oficial, em português) | LICENSE.en.txt (English version).
// Distribuição comercial somente mediante contrato: comercial.nexus.xdata@gmail.com

using System;
using System.Collections.Generic;

namespace NXProject.Models
{
    public class Sprint
    {
        // Numero sequencial (1..N) atribuido na ordem cronologica de inicio.
        public int Number { get; set; }

        // Nome da sprint vindo do DevOps (folha do System.IterationPath).
        // Quando vazio, o nome exibido cai para "Sprint {Number}".
        public string? DisplayName { get; set; }

        // Caminho completo da iteration no DevOps (System.IterationPath),
        // ex.: "Projeto\\Release 1\\Sprint 5". Usado para sincronizar de volta.
        public string? Path { get; set; }

        public DateTime Start { get; set; }
        public DateTime End { get; set; }

        public string Name => string.IsNullOrWhiteSpace(DisplayName)
            ? $"Sprint {Number}"
            : DisplayName!;

        public List<ProjectTask> Tasks { get; set; } = new();
    }
}
