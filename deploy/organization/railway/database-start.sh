#!/bin/sh
set -eu
# The CA is public, but must be read as postgres. The private root signing key is never transmitted.
# Start before certificate creation; requests remain unavailable until the upstream initializer creates a valid root.
runuser -u postgres -- python3 /usr/local/bin/database-trust.py &
exec /usr/local/bin/wrapper.sh "$@"
