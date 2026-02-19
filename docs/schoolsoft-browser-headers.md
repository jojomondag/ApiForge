# SchoolSoft Browser Headers — Problem & Solution

## Problem

SchoolSoft's server (behind a Varnish 6.6 reverse proxy) has a dual routing architecture:
- **Legacy JSP backend** — serves full HTML pages with attendance forms, schedules, etc.
- **Modern React SPA** — serves a minimal 888-byte HTML shell with `<div id="main-root">` and a bundled JS file

When an HTTP request lacks browser-like headers, the server/proxy routes it to the **SPA shell** instead of the JSP backend. This means:
- GET requests return the SPA shell (no schedule data)
- POST requests (e.g., attendance updates) are silently ignored — the server returns HTTP 200 with the SPA shell but does not process the form data

### Symptoms

- POST to `right_teacher_lesson_status.jsp` returns **888 characters** (SPA shell) instead of **~38,000 characters** (JSP page with "Din rapportering har nu sparats")
- Attendance changes appear to succeed (HTTP 200) but the status does not actually change
- Session validation incorrectly reports expired sessions when the session is valid

### Root Cause

The C# `HttpClient` by default sends minimal headers:
- `Content-Type` (from FormUrlEncodedContent)
- `Content-Length` (auto-calculated)
- `Host` (auto-set)

It does **not** send:
- `User-Agent`
- `Accept`
- `Origin`
- `Referer`
- `Sec-Fetch-*` headers

The server uses these headers (likely via Varnish VCL rules) to decide whether to serve the JSP backend or the SPA frontend.

## Solution

### 1. Default headers on `CreateAuthenticatedClient` (TokenManager.cs)

Added browser-like default headers to all requests made through the authenticated client:

```csharp
client.DefaultRequestHeaders.Add("User-Agent",
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
client.DefaultRequestHeaders.Add("Accept",
    "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
client.DefaultRequestHeaders.Add("Accept-Language", "sv-SE,sv;q=0.9");
client.DefaultRequestHeaders.Add("Origin", "https://sms.schoolsoft.se");
client.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "document");
client.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "navigate");
client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
client.DefaultRequestHeaders.Add("Sec-Fetch-User", "?1");
```

### 2. Per-request Referer header on POST (AttendanceUpdater.cs)

The POST request includes a `Referer` header matching what the browser sends (the lesson page URL):

```csharp
var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
request.Headers.Referrer = new Uri(
    $"{_baseUrl}/{schoolSlug}/jsp/teacher/right_teacher_lesson_status.jsp?lesson={detail.LessonId}&teachersubstitute=0&week={detail.Week}");
var resp = await _client.SendAsync(request);
```

### 3. Session validation also needs headers (TokenManager.cs)

`ValidateSessionOnline` creates its own temporary HttpClient. It also needs browser headers, otherwise the server routes validation requests to the login page even when the session is valid:

```csharp
followClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 ...");
followClient.DefaultRequestHeaders.Add("Accept", "text/html,...");
followClient.DefaultRequestHeaders.Add("Accept-Language", "sv-SE,sv;q=0.9");
```

## Required Headers (by importance)

| Header | Required | Why |
|--------|----------|-----|
| `User-Agent` | HIGH | Varnish/WAF may reject or reroute requests without browser UA |
| `Accept` | HIGH | Without `text/html`, server may default to SPA |
| `Origin` | HIGH | CSRF protection; missing Origin on POST is suspicious |
| `Referer` | HIGH (POST) | Server may validate form was submitted from correct page |
| `Sec-Fetch-Dest: document` | MEDIUM | Tells server this is top-level navigation |
| `Sec-Fetch-Mode: navigate` | MEDIUM | Same as above |
| `Sec-Fetch-Site: same-origin` | MEDIUM | Proves request originates from same site |
| `Accept-Language` | LOW | Affects content language |

## How to Debug

The attendance POST always saves the server response to `%TEMP%\schoolsoft_post_debug.html`. Check this file:
- **888 bytes / React SPA shell** = headers are missing, server didn't process the form
- **~38,000 bytes / contains "Din rapportering har nu sparats"** = success

## Additional Issues Found

### Attendance form fields

The `subject-{studentId}` and `subject2-{studentId}` fields must contain the **actual subject ID** (e.g., `27063`), not `"0"`. Setting them to `"0"` causes the server to silently ignore the update.

The `status2_{studentId}` field should be `"0"` for present students and `"1"` for any type of absence.

### Session validation

The validation URL should be the teacher startpage (`right_teacher_startpage.jsp`), not the SMS page. The validation must check for:
- `redirect_login` in the final URL
- `eventMessage=ERR` in the response body
- `Login.jsp` or `location.replace` in the response body

These indicate a definitively expired session (no fallback should be offered).
