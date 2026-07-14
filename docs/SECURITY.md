# Security policy

## Supported code

Security fixes target the current `main` branch and the latest preview/release line. Older preview artifacts may be replaced rather than patched in place.

## Reporting a vulnerability

Do not disclose a suspected vulnerability in a public issue. Use GitHub's **Security → Report a vulnerability** flow for this repository so details can be reviewed privately.

Useful reports include the affected commit/version, host and guest configuration, reproduction steps, expected security boundary, observed behavior, logs with credentials removed, and a minimal proof of impact.

High-priority areas include host containment, process escape, installer or manifest bypass, credential exposure, unsafe agent command classification, evidence isolation, and unauthorized cross-case access.

## Research scope

REPlayer's persona is fingerprint-hardened, not a guarantee against every hardware-attestation, kernel, hypervisor, QEMU-device, or ABI-level detection technique. Please distinguish a fingerprinting bypass from a host-containment vulnerability when reporting.