# Listener ownership for the dev shell. A curl that reaches a port is not
# enough: the listener has to be the process this script started, or a child of it.

listen_inodes() {
  local port="$1" hex
  hex="$(printf '%04X' "$port")"
  awk -v port="$hex" 'FNR > 1 {
    n = split($2, a, ":")
    if (n >= 2 && a[2] == port && $4 == "0A") print $10
  }' /proc/net/tcp /proc/net/tcp6 2>/dev/null
}

listener_pid() {
  local port="$1" inode p fd target
  inode="$(listen_inodes "$port" | head -1)"
  [[ -n "$inode" ]] || return 0
  for p in /proc/[0-9]*; do
    for fd in "$p"/fd/*; do
      target="$(readlink "$fd" 2>/dev/null || true)"
      if [[ "$target" == "socket:[$inode]" ]]; then
        basename "$p"
        return 0
      fi
    done
  done
}

port_is_open() {
  [[ -n "$(listen_inodes "$1" | head -1)" ]]
}

pid_is_under() {
  local pid="$1" root="$2" hops=0 parent
  [[ -n "$pid" && -n "$root" ]] || return 1
  while [[ -n "$pid" && "$pid" != "0" && "$hops" -lt 30 ]]; do
    [[ "$pid" == "$root" ]] && return 0
    parent="$(ps -o ppid= -p "$pid" 2>/dev/null | tr -d ' ')"
    [[ -n "$parent" && "$parent" != "$pid" ]] || return 1
    pid="$parent"
    hops=$((hops + 1))
  done
  return 1
}

require_port_free() {
  local port="$1" label="$2" pid=""
  if port_is_open "$port"; then
    pid="$(listener_pid "$port")"
    if [[ -n "$pid" ]]; then
      echo "admin shell refused to start: ${label} port ${port} is held by pid ${pid}" >&2
    else
      echo "admin shell refused to start: ${label} port ${port} is taken" >&2
    fi
    return 1
  fi
  return 0
}

wait_own_listener() {
  local root="$1" port="$2" label="$3" i lp
  for i in $(seq 1 80); do
    if ! kill -0 "$root" 2>/dev/null; then
      echo "admin shell refused to start: ${label} (pid ${root}) exited before it listened on ${port}" >&2
      return 1
    fi
    if port_is_open "$port"; then
      lp="$(listener_pid "$port")"
      if [[ -z "$lp" ]]; then
        echo "admin shell refused to start: ${label} port ${port} is taken and the listener pid is not visible" >&2
        return 1
      fi
      if pid_is_under "$lp" "$root"; then
        return 0
      fi
      echo "admin shell refused to start: ${label} port ${port} is held by pid ${lp}, not pid ${root}" >&2
      return 1
    fi
    sleep 0.25
  done
  echo "admin shell refused to start: ${label} (pid ${root}) did not listen on ${port}" >&2
  return 1
}
