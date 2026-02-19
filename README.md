# ApiForge

AI-powered .NET library that reverse-engineers API integrations from recorded browser traffic (HAR files). Works with local LLMs (LM Studio) or any compatible AI endpoint.

## How It Works

1. **Record** — Playwright opens a browser, you perform the action, traffic is saved as HAR + cookies
2. **Analyze** — LLM agent identifies the target request, traces dynamic values (tokens, session IDs) back through prior responses, and builds a dependency DAG
3. **Generate** — Optionally produces runnable integration code from the DAG

## Quick Start

```bash
dotnet build
```

### Record

```csharp
var client = new ApiForgeClient();
await client.RecordHarAsync("traffic.har", "cookies.json");
```

### Analyze

```csharp
// Local AI (LM Studio)
var client = new ApiForgeClient(
    apiKey: "lm-studio",
    model: "openai/gpt-oss-20b",
    endpoint: "http://127.0.0.1:1234/v1"
);

var result = await client.AnalyzeAsync(
    prompt: "send a message",
    harFilePath: "traffic.har",
    cookiePath: "cookies.json",
    generateCode: true,
    maxSteps: 25
);
```

### Environment Variables

| Variable | Description |
|---|---|
| `AI_API_KEY` | API key (or `lm-studio` for local) |
| `AI_BASE_URL` | Endpoint (e.g. `http://127.0.0.1:1234/v1`) |
| `AI_MODEL` | Model name |

## Dependencies

- .NET 8.0
- `Microsoft.Playwright` — Browser recording
- `AngleSharp` — HTML parsing (used by demo)

## SchoolSoftDemo

Included demo app that uses ApiForge to interact with SchoolSoft (Swedish school platform):

- **Headless SSO login** via GrandID (username + password + SMS OTP)
- **Session persistence** — SSO cookies (JSESSIONID, Shibboleth session) are saved to `session_tokens.json` and reused across runs. Sessions are validated both locally (age check) and online before reuse
- **Device cookie** — GrandID device cookie saved to `grandid_device.json` after first SMS verification, skipping 2FA on subsequent logins
- **Authenticated HttpClient** — `TokenManager.CreateAuthenticatedClient()` builds an HttpClient with all stored cookies injected, ready to call any SchoolSoft endpoint
- **Schedule fetching** — weekly timetable with day/time/subject/room/group
- **Attendance** — student roster and attendance status per lesson
- **HAR recording** — record new flows with the authenticated session and analyze with local LLM

### Example: Fetch students for a lesson

```
=== Schema ===
Ange vecka (Enter = v9, 'q' = avsluta):

Hittade 16 lektioner:

#    Dag        Tid          Amne                 Sal              Grupp
------------------------------------------------------------------------------------
1    Monday     8:30-9:50    Programmering 1      D215             TE22B
2    Monday     10:10-11:30  Webbutveckling 1     D215             TE22A
3    Tuesday    8:30-9:50    Teknik 1             D215             TE22B
...

Valj lektion (1-16) for narvaro, eller Enter for ny vecka: 1

Hamtar narvaro for Programmering 1 (8:30-9:50)...

=== Programmering 1 — Mandag 8:30 (80 min) ===
    Status: Ej genomford
    Elever: 11

  Namn                           Status
  --------------------------------------------------
  Andersson Erik                 Narvarande
  Berg Sofia                     Narvarande
  Johansson Liam                 Franvarande
  Lindgren Maja                  Sen ankomst
  ...
```

## License

MIT
