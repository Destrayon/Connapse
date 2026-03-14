namespace Connapse.Core.Utilities;

public static class ValidationConstants
{
    // Search
    public const int MaxQueryLength = 10_000;
    public const int MinTopK = 1;
    public const int MaxTopK = 100;
    public const float MinScore = 0.0f;
    public const float MaxScore = 1.0f;

    // Agents
    public const int MinAgentNameLength = 2;
    public const int MaxAgentNameLength = 64;
    public const string AgentNamePattern = @"^[a-zA-Z0-9_-]+$";
    public const int MaxAgentDescriptionLength = 500;
    public const int MaxAgentKeyNameLength = 64;

    // Paths & Files
    public const int MaxPathDepth = 50;
    public const int MaxFileNameLength = 255;
}
