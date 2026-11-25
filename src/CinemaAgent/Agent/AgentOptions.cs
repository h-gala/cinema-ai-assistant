namespace CinemaAgent.Agent;

public class OllamaOptions
{
    public string Endpoint { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "qwen3:8b";
}

public class AgentOptions
{
    public int MaxIterations { get; set; } = 6;
    public int TimeoutSeconds { get; set; } = 180;
    public float Temperature { get; set; } = 0;
    public int Seed { get; set; } = 42;
    /// <summary>Opcjonalne "teraz" (ISO 8601) - do powtarzalnych demo na danych testowych.</summary>
    public string? Now { get; set; }
    /// <summary>Pokazuje wywołania narzędzi, wyniki i pełne błędy. Na produkcji wyłączone.</summary>
    public bool Debug { get; set; } = false;
    /// <summary>Odfiltrowuje filmy z kategorią wiekową wyższą niż wiek użytkownika.</summary>
    public bool EnforceAgeRating { get; set; } = false;
}
