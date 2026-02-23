# Studybee API Map

**Endpoint:** `POST https://backend.studybee.io/graphql`
**Auth:** `Authorization: Token <JWT>`
**Firebase Project:** `studybeereact`

---

## Authentication Flow (DAG)

```
Google OAuth 2.0 (accounts.google.com)
    |
    v (Google access_token: ya29.xxx)
GrandID SAML SSO (saml2.grandid.com / login.grandid.com)
    |
    v (SAML assertion -> Google consent)
getStudybeeTokenByToken(token: google_access_token)
    |
    v (JWT: eyJ0eXAiOiJKV1Qi...)
All subsequent GraphQL queries use: Authorization: Token <JWT>
```

**OAuth Scopes requested:**
- `openid`
- `profile`
- `email`
- `https://www.googleapis.com/auth/classroom.rosters.readonly`
- `https://www.googleapis.com/auth/classroom.profile.emails`

**Client ID:** `1098868114417-4v14h3hpbs7cklu22ore9ofa5pm992la.apps.googleusercontent.com`

**JWT Payload (decoded):**
```json
{
  "iss": "StudyBee",
  "sub": "Insights",
  "userId": 86645,
  "jti": "7b50e22e-22a2-4a20-9938-78345338a50f",
  "iat": 1771862911,
  "exp": 1771866511  // ~1 hour TTL
}
```

---

## GraphQL Queries

### 1. getStudybeeTokenByToken (Token Exchange)

Exchanges a Google OAuth access token for a Studybee JWT.

```graphql
query ($token: String!) {
  getStudybeeTokenByToken(token: $token)
}
```

| Variable | Type | Description |
|----------|------|-------------|
| `token` | `String!` | Google OAuth access token (`ya29.xxx`) |

**Returns:** JWT string (used as `Authorization: Token <jwt>` for all subsequent calls)

---

### 2. userInfo (Current User)

```graphql
{
  userInfo {
    id
    email
    first_name
    last_name
    is_teacher
    is_admin
    whitelisted_domain
    superAdminSchoolIds
    adminSchoolIds
    school
    photo_url
    mentorStudents
    managerSchoolIds
    managerPermissions
    prompt_for_password
    organisations
  }
}
```

**Response fields:**
- `id: Int` - User ID (e.g. 86645)
- `email: String` - Google email
- `is_teacher: Boolean`
- `is_admin: Boolean?`
- `school: { id, name, city, address, features[], primarySchool }`
- `adminSchoolIds: [Int]` - Schools where user is admin
- `mentorStudents: [Int]` - Student IDs this user mentors
- `photo_url: String` - Google profile photo

---

### 3. schools (User's Schools)

```graphql
{
  schools {
    id
    name
    isSuperAdmin
    isAdmin
    isTeacher
    isManager
    educational_system_id
    educational_system_name
    read_only_graduation_prognosis
    features { id, name, description }
  }
}
```

**Response:** Array of School objects with role flags per school.

**Features observed:**
- `GraduationPrognosis` (id: 1)
- `StudentCase` (id: 2)

---

### 4. GetTotalUnreadMessages

```graphql
query GetTotalUnreadMessages($schoolId: Int!) {
  getTotalUnreadMessages(schoolId: $schoolId)
}
```

| Variable | Type | Description |
|----------|------|-------------|
| `schoolId` | `Int!` | School ID (e.g. 6624) |

**Returns:** `Int` (count of unread messages)

---

### 5. CasesListForUnread (Student Cases)

```graphql
query CasesListForUnread($schoolIds: [Int]!, $userId: Int) {
  casesListForUnread(schoolIds: $schoolIds, userId: $userId) {
    id
    modified
    latestCommentModified
    latestAttachmentModified
    authorUserId
  }
}
```

| Variable | Type | Description |
|----------|------|-------------|
| `schoolIds` | `[Int]!` | Array of school IDs |
| `userId` | `Int` | User ID (optional filter) |

---

### 6. ewsFilter (Early Warning System - Full Student List)

**Largest response observed: 352 KB** - contains all classrooms and student IDs.

```graphql
query ($schoolId: Int!, $includeStudentId: Boolean) {
  ewsFilter(schoolId: $schoolId, includeStudentId: $includeStudentId)
}
```

| Variable | Type | Description |
|----------|------|-------------|
| `schoolId` | `Int!` | School ID |
| `includeStudentId` | `Boolean` | Include student IDs in response |

**Response structure:**
```json
{
  "classrooms": [
    {
      "id": "MjM0MzM4MDk2NjVa",   // Base64 encoded classroom ID
      "name": "1NATEK3 25/26 Ma1c",
      "archived": false,
      "ignored": false,
      "room": "",
      "section": "",
      "students": [
        { "id": 96182531 },
        { "id": 74165961 }
      ]
    }
  ]
}
```

---

### 7. ewsDataByStudents (Student EWS Data)

**Large response: ~37-42 KB per student** - contains courses, assessments, adjustments.

```graphql
query ($schoolId: Int!, $studentIds: [Int], $studentListIds: [Int], $getMentorStudents: Boolean) {
  ewsDataByStudents(
    schoolId: $schoolId
    studentIds: $studentIds
    studentListIds: $studentListIds
    getMentorStudents: $getMentorStudents
  ) {
    extraAdjustments
    courses
    filters
    students {
      id
      google_id
      first_name
      last_name
      photo_url
      email
      courseStatus
      courseAssessments
      extraAdjustmentsLog
      statusLog
      commentsLog
    }
  }
}
```

| Variable | Type | Description |
|----------|------|-------------|
| `schoolId` | `Int!` | School ID |
| `studentIds` | `[Int]` | Specific student IDs |
| `studentListIds` | `[Int]` | Student list/group IDs |
| `getMentorStudents` | `Boolean` | Get mentor's students |

**Response includes:**
- `extraAdjustments[]` - Available extra adjustments (id, name)
  - "Hjalp med att planera och strukturera ett schema over skoldagen"
  - "Ge extra tydliga instruktioner"
  - "Ge stod for att satta igang arbetet"
  - "Ge ledning i att forsta texter"
  - etc.
- `courses[]` - Course definitions
- `students[].courseStatus` - Per-course status
- `students[].courseAssessments` - Per-course assessments
- `students[].extraAdjustmentsLog` - History of adjustments
- `students[].statusLog` - Status change history
- `students[].commentsLog` - Comments history

---

### 8. GetTestResults

```graphql
query GetTestResults($studentIds: [Int]!, $schoolId: Int!, $sortGroup: String) {
  getTestResults(
    studentIds: $studentIds
    schoolId: $schoolId
    sortGroup: $sortGroup
  ) {
    id
    results { id }
  }
}
```

| Variable | Type | Description |
|----------|------|-------------|
| `studentIds` | `[Int]!` | Student IDs |
| `schoolId` | `Int!` | School ID |
| `sortGroup` | `String` | Optional sort group |

---

### 9. studentEws (Single Student Detail)

```graphql
query ($schoolId: Int!, $studentId: Int!) {
  studentEws(schoolId: $schoolId, studentId: $studentId) {
    studentLists { id, name, source, source_id, school_id }
    mentors { id, first_name, last_name, email }
    first_name
    last_name
    municipality
    municipality_code
    email
    google_id
    photo_url
    extraAdjustmentText
    courseStatus { courseId, courseName, subjectName, archived, ignored }
    extraAdjustments { id, name }
    extraAdjustmentsLog {
      id, name, free_text, status, created, modified,
      admin_id, course_definition_id, extra_adjustment_id,
      student_id, studentGlobal, commentCount,
      user { id, first_name, last_name, photo_url }
    }
  }
}
```

| Variable | Type | Description |
|----------|------|-------------|
| `schoolId` | `Int!` | School ID |
| `studentId` | `Int!` | Student ID |

**Response includes:**
- Student lists (source: "ACADEMEDIA SYNC")
- Mentors assigned to student
- Student personal info (name, email, google_id, municipality)
- Course status per course
- Extra adjustments history with comments

---

## Key IDs Observed

| Entity | ID | Value |
|--------|-----|-------|
| School (NTI Helsingborg) | schoolId | `6624` |
| School (NTI Vetenskapsgymnasiet) | schoolId | `61587` |
| User (Josef Nobach) | userId | `86645` |
| Student example | studentId | `74165932`, `74165936` |

---

## Firebase Configuration

```json
{
  "projectId": "studybeereact",
  "appId": "1:804858914282:web:e535bafa513764681dbe93",
  "databaseURL": "https://studybeereact.firebaseio.com",
  "storageBucket": "studybeereact.firebasestorage.app",
  "authDomain": "studybeereact.firebaseapp.com",
  "messagingSenderId": "804858914282",
  "measurementId": "G-9L81VPN6EY"
}
```

---

## Infrastructure Notes

- **Error tracking:** Sentry at `sentry.studybee.io` (API key: `4aed336f550b...`)
- **Frontend:** React SPA at `insights.studybee.io`
- **Backend:** `backend.studybee.io` (GraphQL)
- **Auth chain:** Google OAuth -> GrandID SAML (AcadeMedia federation) -> Google consent -> Studybee JWT
- **JWT TTL:** ~1 hour (3600 seconds)
