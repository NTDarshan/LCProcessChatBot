namespace backend.Dtos;

public class ClarificationDto
{
    // "bank" | "customer" | "lc_number"
    public string EntityType { get; set; } = "";

    // The value the user mentioned that was not found in the database
    public string UnrecognisedValue { get; set; } = "";

    // Real values from the database for the user to choose from
    public string[] AvailableOptions { get; set; } = [];

    // The original question — frontend replaces unrecognised value with the clicked option
    public string QuestionTemplate { get; set; } = "";
}
