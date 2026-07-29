#!/bin/bash
# Generate the SSH host keys when they are missing, before sshd is started.
#
# Why this exists: a shipped image must NOT carry host keys (every flashed
# card would share one identity, and anyone with the image could impersonate
# every device built from it), but something has to create them on the first
# boot of each device. Debian leaves that to the openssh-server postinst,
# which never runs again on an already-installed system, and the images
# Polaris ships had no key-generating unit at all. The result was silent and
# total: `ssh.service` has `ExecStartPre=/usr/sbin/sshd -t`, which fails
# without a host key, so sshd died at every boot and port 22 answered
# "connection refused" on a headless board (field report, Orange Pi 4 Pro).
#
# `ssh-keygen -A` creates only the key types that are missing, so this is
# idempotent by construction: on a host that already has its keys it is a
# no-op, and it never replaces an existing key.
set -u

log() { echo "polaris-sshkeys: $*"; }

command -v ssh-keygen >/dev/null 2>&1 || { log "ssh-keygen not installed, nothing to do"; exit 0; }
[ -d /etc/ssh ] || { log "/etc/ssh does not exist, openssh-server is not installed"; exit 0; }

count_keys() { ls /etc/ssh/ssh_host_*_key 2>/dev/null | wc -l; }

before="$(count_keys)"
if ! ssh-keygen -A; then
    # Never fail the unit: a boot that cannot make keys should still reach
    # multi-user, it just has no SSH. The message is the point.
    log "ssh-keygen -A failed, sshd will not start until host keys exist"
    exit 0
fi
after="$(count_keys)"

if [ "$after" -gt "$before" ]; then
    log "generated $(( after - before )) host key(s), sshd can start now"
else
    log "host keys already present ($after), nothing to do"
fi
exit 0
