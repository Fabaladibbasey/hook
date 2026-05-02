---

# 📦 WhatsApp Multi-Service Connector — Requirements

## 🎯 Goal

Build a **WhatsApp-based multi-service matching system** that connects clients with nearby service providers (delivery, plumbing, carpentry, etc.) using:

* AI-powered conversation (intent detection & clarification)
* Distance-aware matching
* Proactiveness-based ranking
* Privacy-controlled communication (phone or chat link)
* Optional real-time chat (SignalR)

### Core Principles

* No login required
* No app install required
* Minimal data collection
* Fast connection between parties
* Platform exits after connection

---

# 🧠 AI-Driven Conversation Layer

## Model

* Use AI model (e.g. Gemini)
* Model choice is interchangeable

---

## Responsibilities

### AI Handles:

* Intent detection (client & provider)
* Service extraction from natural language
* Entity extraction (location, service)
* Clarification questions when uncertain

---

### System Handles (Deterministic):

* Matching logic
* Distance calculation
* Ranking logic
* Privacy rules
* Chat lifecycle rules

---

## Rules

* Always confirm critical fields:

  * service type
  * location

* If confidence is low:
  → ask clarification

* Never silently assume

---

## Example

User:

> “My sink is leaking”

System:

> “Do you need a plumber?”

---

# 🔁 END-TO-END FLOW

## 1. Provider Availability (Supply)

### Input (via WhatsApp):

* Free text:

  * “I’m available for delivery”
  * “I fix doors and repair laptops”

---

## Processing

* AI extracts:

  * services[]

---

## Confirmation

> “I detected: Carpentry, Computer Repair. Is this correct? (YES / EDIT)”

---

## Collect

* Location (GPS preferred)
* Contact sharing consent (YES / NO)

---

## Store

* phone
* services[]
* location (lat/lng)
* share_contact (bool)
* last_active_at
* expires_at (24h)

---

# 2. Client Request (Demand)

### Input (free text):

* “I need a plumber”
* “My laptop is broken”

---

## Processing

* AI detects service

---

## Confirmation

> “Do you need a Plumber?”

---

## Collect

* Location (required)
* Optional description

---

## Store

* service_request

---

# 🧠 SMART SERVICE SYSTEM

## Approach

* Hybrid system:

  * Canonical service list
  * AI mapping + fuzzy matching

---

## Example Services

* delivery
* plumbing
* carpentry
* painting
* computer repair

---

## Rule

* Always confirm detected service before proceeding

---

# 🧮 MATCHING ENGINE

## Inputs

* service_type
* client_location
* provider_location

---

## Filtering

* active providers (not expired)
* providers offering service
* providers with location

---

## Distance Calculation

* Haversine formula OR PostGIS

---

## Ranking Formula

```plaintext
score =
  (distance_weight * proximity_score) +
  (activity_weight * recency_score) +
  (success_weight * success_rate)
```

---

## Output

* Top **2–3 providers**

---

# 🔁 MATCH ITERATION

## Client Controls

Client can request more matches:

* “NEXT”
* “MORE”
* “NOT THESE”

---

## System Behavior

* Exclude already shown providers
* Return next ranked providers

---

# 🔗 CONNECTION RULES

## Case A — Contact Sharing Enabled (both)

* Share phone numbers both ways

---

## Case B — Contact Sharing Disabled (either)

👉 Enforced rule:

* BOTH must use chat link

---

## 🚫 Case C — Mixed

* Not allowed
* Never exists

---

# 🔐 CHAT SYSTEM (SignalR)

## Technology

* .NET SignalR (real-time messaging)

---

## Chat Link Structure

Client:

```
/c/{chatId}/{clientToken}
```

Provider:

```
/c/{chatId}/{providerToken}
```

---

## Rules

* Each participant gets unique link
* Links map to same chat session
* Tokens are:

  * signed
  * expirable

---

# 💬 CHAT LIFECYCLE

## ACTIVE

* Real-time messaging enabled

---

## IDLE FLOW

### After ~20 minutes inactivity:

Send reminder:

> “Are you still available? Reply to continue.”

---

### After 30 minutes total inactivity:

* status → `ended`
* chat permanently closed

---

## ENDED

* Triggered by:

  * manual end
  * idle timeout

* ❌ No messages allowed

---

## EXPIRED

* After 24 hours
* ❌ No messages allowed

---

# ❌ MESSAGE RULES

No messages allowed when:

* chat = ended
* chat = expired

---

# 👤 PARTICIPANT SESSION RULES

## Single Active Session Per Participant

* If link is opened again:

  * old session revoked (only for that participant)
  * new session becomes active

---

## Goal

* Prevent:

  * link sharing
  * third-party listening

---

# 📊 CHAT ACCESS LOGGING

Track:

* participant_id
* chat_id
* opened_at
* ip_address
* device_info

---

## Rule

* Last opened session overrides previous

---

# 📍 LOCATION STRATEGY

## Provider

* Must provide location
* Stored as last_known_location
* Expires with availability (24h)

---

## Client

* Must provide location

---

## Optional

Client can request:

> “Increase range”

---

# 🔁 FEEDBACK SYSTEM

## Step 1

> “Did you find a service provider?” (YES / NO)

---

## Step 2

> “Was the job completed?”

Options:

* YES
* NO
* IN PROGRESS

---

## Store

* success_rate
* completion_rate

---

# ⚠️ GUARDRAILS

## Spam Protection

* Rate limit:

  * availability updates
  * requests

---

## Match Control

* Max 2–3 providers per batch

---

## Privacy

* No number sharing without consent
* Enforce symmetric sharing rules

---

## Link Security

* Signed tokens
* Expiry enforced

---

# 🧱 DATA MODEL

## provider_availability

* phone
* services[]
* location
* share_contact
* last_active_at
* expires_at

---

## service_request

* id
* client_phone
* service_type
* location
* description
* created_at

---

## match

* id
* request_id
* provider_phone
* service_type
* distance
* score
* contact_shared
* chat_id

---

## chat_session

* id
* status (active | ended | expired)
* expires_at
* last_activity_at

---

## chat_participant

* id
* chat_id
* role (client | provider)
* phone (nullable)
* token
* is_active_session

---

## chat_message

* id
* chat_id
* participant_id
* message
* created_at

---

## chat_access_log

* id
* participant_id
* opened_at
* ip_address
* device_info

---

# 🚀 FINAL SUMMARY

This system is:

> A **WhatsApp-first, AI-powered, multi-service connector** with:

* Smart intent detection
* Distance-based matching
* Proactiveness-based ranking
* Iterative provider discovery
* Privacy-controlled communication
* Secure, ephemeral real-time chat

---

# 🏷️ NAME OPTIONS (OPEN)

You selected direction:

* Hook
* LinkUp
* Reach
* Bridge
* CallUp

👉 Final name is intentionally **not decided**.
