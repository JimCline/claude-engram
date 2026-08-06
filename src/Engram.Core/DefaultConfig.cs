namespace Engram.Core;

public static class DefaultConfig
{
    public static string Content => $"""
        [embedding]
        # The vector lane is optional. "none" is a supported configuration, not a degraded
        # one — recall works lexically without it. Turning it on is a tradeoff in disk,
        # memory and startup time, so it is a choice rather than a default.
        #
        #   none           lexical recall only
        #   local          a model Engram runs itself, from the list below
        #   openai-compat  anything serving POST /v1/embeddings — LM Studio, llama.cpp's
        #                  server, vLLM, OpenAI, Voyage, most hosted providers
        #   ollama         Ollama's native batch endpoint
        #
        # Note: Anthropic publishes no embeddings API. Claude cannot be a provider here.
        provider = "none"

        # -- local --
        # Smallest to largest. Width comes from the model, so `dim` is not needed:
        #   all-minilm-l6-v2       384d   ~25 MB   256-token window, English
        #   nomic-embed-text-v1.5  768d  ~140 MB  8k-token window, English
        #   qwen3-embedding-0.6b  1024d  ~610 MB  32k-token window, 100+ languages
        model = "nomic-embed-text-v1.5"

        # -- openai-compat / ollama --
        # endpoint = "http://localhost:1234/v1"   # LM Studio, vLLM, llama.cpp server
        # endpoint = "http://localhost:11434"     # Ollama
        # model = "nomic-embed-text-v1.5"         # whatever the endpoint calls it
        # dim = 768                               # required: the width it returns
        # api_key_env = "OPENAI_API_KEY"          # the NAME of the variable, never the key
        # timeout_seconds = 60

        # -- shared --
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
        roots = ["/knowledge", "/user", "/sessions", "/machine", "/projects", "/people", "/concepts"]
        default_user = "jim"          # preference-kind facts route to /people/<default_user>/…
        # /knowledge holds the seeded corpus; /user holds what the user stated about
        # themselves, one entity per statement. Both were in use before they were listed
        # here — this list is documentation, not a constraint anything enforces yet.
        # Code is not a root: a codebase belongs to a project, so it lands at
        # /projects/<name>/code/<repo>/… beside that project's decisions (D27).

        [primer]
        max_tokens = 300
        """;
}
