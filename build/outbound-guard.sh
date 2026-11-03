# Sourced by the `game-service-isolation` steps in .github/workflows/build.yml (G3 S3.8). Reads the
# packet counters of the owner-match chain that job installs, once per address family.
#
#   guard_total <4|6> <TARGET>   packets the chain's rules with that target (REJECT or RETURN) matched
#
# The chain is `iptables`/`ip6tables` -L output: column 1 is the packet count, column 3 the target.

GUARD_CHAIN=PLATFORM_S3_OFFLINE
GUARD_USER=platform-s3-offline

guard_tool() {
  case "$1" in
    4) echo iptables ;;
    6) echo ip6tables ;;
    *) echo "guard_tool: family must be 4 or 6, got '$1'" >&2; return 1 ;;
  esac
}

guard_total() {
  sudo "$(guard_tool "$1")" -L "$GUARD_CHAIN" -v -x -n |
    awk -v target="$2" '$3 == target { total += $1 } END { print total + 0 }'
}
