# Verified map navigation implementation

Scope follows the approved expanded mapper design. Keep changes in the shared checkout.

1. Add loopback regression tests for one-step acknowledgements, wrong/burst arrivals, blocked output, private input, timeout, cancellation and whole-route validation.
2. Expose Telnet negotiation evidence independently of room metadata and surface every private interval before delivering room updates.
3. Queue room observations with other bounded session output. Publish received-field evidence and preserve observation order.
4. Implement controller walking with whole-route validation, canonical direction commands, graph/session/privacy guards, cancellable per-step acknowledgement and explicit localized stop reasons. Bypass aliases; any competing command takes over.
5. Run targeted desktop and Telnet tests in coordination with the parent build owner, then hand over final files and any coverage limits.
