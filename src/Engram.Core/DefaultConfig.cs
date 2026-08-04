namespace Engram.Core;

public static class DefaultConfig
{
    public static string Content => $"""
        [embedding]
        provider = "none"            # none | llamasharp | openai-compat
        # -- llamasharp --
        model_path = "~/{EngramHome.DirectoryName}/models/qwen3-embedding-0.6b-q8_0.gguf"
        threads = 4
        idle_unload_minutes = 5
        # -- openai-compat --
        endpoint = "http://localhost:1234/v1"
        model = "text-embedding-qwen3-embedding-0.6b"
        # -- shared --
        dim = 1024
        max_batch = 16

        [retrieval]
        default_budget_tokens = 500
        seed_k = 32
        graph_hops = 2
        recency_half_life_days = 45

        [indexing]
        auto_index_on_session_start = true
        max_sync_index_ms = 1500      # beyond this, indexing continues async
        ignore = ["**/bin/**", "**/obj/**", "**/node_modules/**", "**/.git/**"]

        [impressions]
        mode = "extractive"           # extractive (default, zero-dep) | llm
        # llm mode refines extractive impressions via an OpenAI-compatible local endpoint
        endpoint = "http://localhost:1234/v1"
        model = "qwen3-4b-instruct"
        max_tokens_per_impression = 60
        batch = 8                      # throttled, idle-priority, never blocks indexing

        [taxonomy]
        # top-level roots of the memory tree; extendable without migration
        roots = ["/machine", "/code", "/projects", "/people", "/concepts"]
        default_user = "jim"          # preference-kind facts route to /people/<default_user>/…

        [primer]
        max_tokens = 300
        """;
}
