namespace backend.Dtos;

public class ProcessingStageUpdateDto
{
    public int Sequence { get; set; }
    public string StageKey { get; set; } = string.Empty;
    public string StageName { get; set; } = string.Empty;
    public string? LiveLabel { get; set; }
    public string Status { get; set; } = "in_progress";
    public int ProgressPercent { get; set; }
    public int? EstimatedMsRemaining { get; set; }
    public long? ElapsedMs { get; set; }
    public string? ErrorMessage { get; set; }
    public Dictionary<string, string>? TechnicalDetails { get; set; }
}

