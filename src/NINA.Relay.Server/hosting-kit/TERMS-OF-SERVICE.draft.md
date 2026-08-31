# Polaris Relay: Terms of Service (DRAFT)

> Draft for the operator to adapt before launch. Fill in the operator
> identity, jurisdiction, and contact. This is a template, not legal
> advice.

**Operator:** [name / entity], contact: [email]
**Service:** the shared relay at `relay.[domain]` ("the Service"), which
forwards traffic between your web browser and your own Polaris device
over an outbound tunnel, so the device is reachable from the internet
without router configuration.

## 1. What the Service is

The Service is a network intermediary. It does not host your Polaris
software, your images, or your account: those live on your own device.
The Service only carries traffic between your browser and that device
while your access token is valid.

## 2. Free, best-effort, no warranty

The Service is provided free of charge, as-is, with no uptime guarantee
and no warranty of any kind. It may be interrupted for maintenance,
throttled, or discontinued. Do not depend on it for safety-critical
operation of equipment; always have local access to your device. To the
maximum extent permitted by law, the operator is not liable for damage
to equipment, loss of data, or missed observations arising from use or
unavailability of the Service.

## 3. Your token and your responsibilities

- Your access token identifies your device on the Service. Keep it
  secret; anyone holding it can reach your device's login page. Your
  device's own password remains your responsibility.
- You may only tunnel devices you own or are authorized to control.
- Fair-use limits apply per token: request rate, bandwidth, and a
  monthly transfer quota. Exceeding the quota suspends the tunnel until
  the monthly reset. Deliberately circumventing limits, sharing tokens,
  or using the Service to carry traffic unrelated to Polaris is grounds
  for revocation.

## 4. Suspension and termination

The operator may revoke a token at any time for abuse, security
concerns, or discontinuation of the Service, and will make reasonable
efforts to announce planned shutdowns in advance in the project's
community channels. You may stop using the Service at any time by
disabling the relay in Polaris settings; you can always self-host the
relay software instead, which is free and open source.

## 5. Privacy

Operation of the Service involves processing connection metadata as
described in the Privacy Policy published alongside these terms.

## 6. Changes

These terms may change; the current version is published at
[URL]. Continued use after a change constitutes acceptance.

*Last updated: [date]*
