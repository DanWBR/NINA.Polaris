# Polaris Relay: Privacy Policy (DRAFT)

> Draft for the operator to adapt before launch. Written to be honest
> about what a TLS-terminating relay can and cannot see. Review against
> GDPR/LGPD obligations for your jurisdiction before publishing. This is
> a template, not legal advice.

**Controller:** [name / entity], contact: [email]

## What the relay handles

The relay forwards traffic between your browser and your Polaris device.
HTTPS from your browser terminates at the relay, and the relay forwards
the request to your device through an encrypted tunnel. This means the
relay processes your traffic in memory while forwarding it, like any
reverse proxy. The relay does not store, inspect, or log the content of
that traffic: no images, no page contents, no request bodies, no
passwords are recorded.

## What is stored

- **Tenant record:** your chosen hostname slug, your access token, the
  tier limits, an optional note (typically your community handle and the
  issue date), and an optional expiry date.
- **Usage counters:** total bytes transferred per month per token, kept
  to enforce the monthly quota.
- **Audit log:** one line per forwarded request with timestamp, tenant
  slug, HTTP method and path, status code, request and response sizes,
  duration, your browser's IP address, and user agent. Request and
  response bodies are never logged. The log is capped in size and old
  entries are overwritten as it rotates (on the order of weeks under
  normal traffic).
- **Server logs:** standard service logs (connections, errors) retained
  briefly for operations.

## What is NOT stored

No account, no email requirement beyond the channel you used to request
a token, no analytics, no cookies set by the relay, no traffic content,
and no data about your Polaris device beyond the tunnel connection
itself. Your observatory's coordinates live in your device, not in the
relay.

## Why (legal basis)

The data above is processed to operate the service you requested
(providing the tunnel, enforcing fair-use quotas) and to protect it
(abuse detection via the audit log). That is performance of the service
and legitimate interest in its security.

## Sharing

Nothing is sold or shared with third parties. Infrastructure providers
(the VPS host, and the CDN/DNS provider if one fronts the relay:
currently [provider names]) process traffic as any network carrier does,
under their own policies. Data may be disclosed if the law requires it.

## Your rights and contact

Ask [email] to see or delete your tenant record and usage data; deleting
it revokes the tunnel. Audit log entries age out on rotation and cannot
be selectively edited; a deletion request stops new entries by revoking
the token.

*Last updated: [date]*
