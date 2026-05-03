namespace backend.Models;

// Holds the SQL query associated with a detected intent
public class IntentDefinition
{
    // Human-readable intent name (matches the value returned by AI classifier)
    public string Name { get; set; } = string.Empty;

    // SQL query to execute when this intent is matched
    public string Sql { get; set; } = string.Empty;
}
