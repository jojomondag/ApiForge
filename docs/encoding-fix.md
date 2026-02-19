# Encoding Fix: Swedish Characters (Å Ä Ö)

## Problem

Swedish characters (Å, Ä, Ö, å, ä, ö) appeared garbled when fetching HTML from SchoolSoft:

| Expected | Displayed |
|----------|-----------|
| LÄRTEAM | LÃ„RTEAM |
| Ahlström | AhlstrÃ¶m |
| Bäckström | BÃ¤ckstrÃ¶m |

## Root Cause

Two separate encoding corruption points working together:

### 1. HttpClient.GetStringAsync trusts the server's charset lie

SchoolSoft's server sends `Content-Type: text/html; charset=ISO-8859-1` but the actual byte encoding varies (sometimes UTF-8, sometimes real ISO-8859-1).

`HttpClient.GetStringAsync` reads the charset from the Content-Type header and decodes accordingly. When the server lies about the encoding, the bytes get decoded with the wrong charset, producing mojibake.

**Source**: [.NET Runtime HttpContent.cs](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Net.Http/src/System/Net/Http/HttpContent.cs) — `ReadBufferAsString` prioritizes Content-Type charset over BOM detection and UTF-8 default.

### 2. AngleSharp re-encodes based on `<meta charset>` in the HTML

Even if you fix the encoding before passing HTML to AngleSharp, it undoes the fix:

1. `VirtualResponse.Content(string)` converts the string to UTF-8 bytes in a `MemoryStream`
2. AngleSharp creates a `WritableTextSource` with `EncodingConfidence.Tentative`
3. The parser encounters `<meta http-equiv="Content-Type" content="text/html; charset=ISO-8859-1">`
4. `EncodingMetaHandler` fires and sets `CurrentEncoding` to ISO-8859-1
5. `WritableTextSource` **re-decodes all raw bytes as ISO-8859-1**, corrupting the text again

**Source**: [AngleSharp Issue #889](https://github.com/AngleSharp/AngleSharp/issues/889), [Issue #86](https://github.com/AngleSharp/AngleSharp/issues/86)

## Solution

Two changes, both required:

### Fix 1: Bypass GetStringAsync — use GetByteArrayAsync + auto-detect

```csharp
// Instead of:
var html = await _client.GetStringAsync(url);

// Use:
var bytes = await _client.GetByteArrayAsync(url);
var html = DecodeHtml(bytes);
```

The `DecodeHtml` method tries UTF-8 first, falls back to Windows-1252:

```csharp
internal static string DecodeHtml(byte[] bytes)
{
    var utf8 = Encoding.UTF8.GetString(bytes);
    if (!utf8.Contains('\uFFFD'))
        return utf8;
    return Encoding.GetEncoding(1252).GetString(bytes);
}
```

Why auto-detect: SchoolSoft's encoding is inconsistent. Some responses are UTF-8, others are ISO-8859-1/Windows-1252. The `U+FFFD` check detects invalid UTF-8 sequences and falls back.

**Requires** `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` at startup for Windows-1252 support in .NET 8.

### Fix 2: Disable AngleSharp's EncodingMetaHandler

```csharp
using AngleSharp.Browser;

// Instead of:
var config = Configuration.Default;

// Use:
var config = Configuration.Default.Without<EncodingMetaHandler>();
```

This prevents AngleSharp from reacting to `<meta charset>` tags and re-decoding the already-correct text.

## Why earlier approaches failed

| Approach | Why it failed |
|----------|--------------|
| `FixMojibake` (Latin1.GetBytes → UTF8.GetString) | Worked in theory, but AngleSharp undid the fix (point 2 above). Also fragile: one invalid byte in the entire HTML caused silent fallback. |
| `GetStringAsync` + manual re-encoding | Can't override encoding used by `GetStringAsync` — it's determined internally with no extension point. |
| `Console.OutputEncoding = UTF8` | Console was never the issue — hardcoded Swedish chars displayed fine. |
| Pure `Encoding.UTF8.GetString(bytes)` | Failed when server sent actual ISO-8859-1 bytes (single-byte Ä = 0xC4 is not valid UTF-8). |

## Files changed

- `ScheduleFetcher.cs` — `GetByteArrayAsync` + `DecodeHtml`, `Without<EncodingMetaHandler>()`, removed `FixMojibake`
- `AttendanceFetcher.cs` — `GetByteArrayAsync` + `DecodeHtml`, `Without<EncodingMetaHandler>()`
- `Program.cs` — `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` + UTF-8 console output

## Key takeaways

1. Never trust `GetStringAsync` when scraping — servers lie about charset
2. AngleSharp's `req.Content(string)` is NOT safe from re-encoding — it converts to bytes internally
3. Auto-detect encoding (try UTF-8, fallback to Windows-1252) handles inconsistent servers
4. Always disable `EncodingMetaHandler` when you control the encoding yourself
