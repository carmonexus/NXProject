namespace NXProject.Models
{
    public class DevOpsProject
    {
        public string Name             { get; set; } = "";
        public int    RootWorkItemId   { get; set; }
        // Owner (System.AssignedTo) do work item raiz — informativo, vindo do Discovery/import.
        public string Owner            { get; set; } = "";
        public bool   IsOpex           { get; set; } = true;
        public string CostCenter       { get; set; } = "";
        // "CAPEX", "OPEX" ou "EPIC" (lê do campo Tipo_Centro_Custo de cada EPIC).
        // String vazia/null = derivado de IsOpex para compatibilidade.
        public string CostCenterSource { get; set; } = "";

        // Processo do Team Project no DevOps (Agile, Scrum, CMMI, Basic). Informativo,
        // vindo do Discovery/import. Vazio = desconhecido (entrada manual sem leitura).
        public string Process { get; set; } = "";

        // Nome com o owner anexado, para exibição em listas/combos.
        [System.Text.Json.Serialization.JsonIgnore]
        public string DisplayName =>
            string.IsNullOrWhiteSpace(Owner) ? Name : $"{Name} — Owner: {Owner}";

        public override string ToString() => Name;
    }
}
