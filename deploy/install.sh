#!/usr/bin/env bash
# Installs or upgrades the KnS server from this package on a systemd Linux machine (Debian/Ubuntu tested).
#
#   sudo ./install.sh                    install or upgrade; keeps /etc/kns/kns.env and the database
#   sudo ./install.sh --reset-progress   also resets every player's progress afterwards (database backed up first)
#
# Layout: /opt/kns/{server,web,tools} (replaced on upgrade, previous copy restored if the new one fails),
# /etc/kns/kns.env (settings, created from kns.env.example once), /var/lib/kns (database, owned by user kns),
# systemd unit kns-server. Nothing else on the machine is touched: put a reverse proxy in front yourself
# (deploy/README.md shows Caddy and nginx).
set -Eeuo pipefail
cd "$(dirname "$0")"
reset=false
for arg in "$@"; do
    case "$arg" in
        --reset-progress) reset=true ;;
        *) echo "Unknown argument: $arg" >&2; exit 2 ;;
    esac
done
[[ $EUID -eq 0 ]] || { echo 'Run as root (sudo ./install.sh).' >&2; exit 1; }
command -v systemctl >/dev/null || { echo 'systemd is required (or use docker compose).' >&2; exit 1; }
command -v curl >/dev/null || { echo 'curl is required.' >&2; exit 1; }
test -x server/KnS.Server && test -s web/index.html && test -s deploy/kns.env.example || {
    echo 'Run from an extracted package (build.bat server): server/, web/, deploy/ are missing.' >&2; exit 1; }
if find server web -type f \( -name '*.sqlite*' -o -name '*.db' \) | grep -q .; then
    echo 'Refusing a package that contains a database.' >&2; exit 1
fi

exec 9>/run/lock/kns-install.lock
flock -n 9 || { echo 'Another KnS install is running.' >&2; exit 1; }

id kns >/dev/null 2>&1 || useradd --system --home-dir /var/lib/kns --shell /usr/sbin/nologin kns
install -d -o kns -g kns -m 750 /var/lib/kns
install -d -o root -g kns -m 750 /etc/kns

env_file=/etc/kns/kns.env
if [[ ! -f $env_file ]]; then
    install -o root -g kns -m 640 deploy/kns.env.example "$env_file"
    echo "Created $env_file from the example; edit it, then run this again to apply changes."
fi
if grep -Eq '^KNS_ADMIN_KEY=$' "$env_file"; then
    key=$(head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n')
    sed -i "s/^KNS_ADMIN_KEY=$/KNS_ADMIN_KEY=$key/" "$env_file"
    echo "Generated KNS_ADMIN_KEY in $env_file."
fi
setting() {
    (
        set -a
        # shellcheck source=/dev/null
        . "$env_file"
        set +a
        printf '%s' "${!1:-$2}"
    )
}
bind=$(setting KNS_BIND 127.0.0.1)
port=$(setting KNS_PORT 8092)
probe_host="$bind"
if [[ $probe_host == "0.0.0.0" || $probe_host == "::" || $probe_host == "*" || -z $probe_host ]]; then
    probe_host=127.0.0.1
fi
db=$(setting KNS_DB /var/lib/kns/kns.sqlite)
case "$db" in /var/lib/kns/*) ;; *) echo "KNS_DB must live under /var/lib/kns (the only writable folder): $db" >&2; exit 1 ;; esac

port_in_use() {
    if command -v ss >/dev/null 2>&1; then
        ss -H -ltn "sport = :$1" | grep -q .
    elif command -v python3 >/dev/null 2>&1; then
        python3 -c 'import socket, sys; s = socket.socket(); s.settimeout(0.2); sys.exit(0 if s.connect_ex(("127.0.0.1", int(sys.argv[1]))) == 0 else 1)' "$1"
    else
        (exec 3<>"/dev/tcp/127.0.0.1/$1") >/dev/null 2>&1
    fi
}

healthy() {
    local url=$1
    for _ in $(seq 1 40); do
        if curl -fs --max-time 2 -o /dev/null "$url"; then return 0; fi
        sleep 0.5
    done
    return 1
}

# Start the new build on disposable data first: a broken package never stops the running server.
candidate=/opt/kns.next
rm -rf -- "$candidate"
mkdir -p "$candidate"
cp -a server web "$candidate/"
[[ -d tools ]] && cp -a tools "$candidate/"
chown -R root:root "$candidate"
chmod -R a+rX "$candidate"
preflight=$(mktemp -d /tmp/kns-preflight-XXXXXXXX)
chown kns:kns "$preflight"
preflight_port=18099
while port_in_use "$preflight_port"; do preflight_port=$((preflight_port + 1)); done
# Started inside the scratch folder: the server's content root is its working directory, which user kns must be able to read.
# shellcheck disable=SC2016
runuser -u kns -- env -i PATH=/usr/bin:/bin KNS_BIND=127.0.0.1 KNS_PORT=$preflight_port KNS_QUIET=1 \
    KNS_DB="$preflight/kns.sqlite" KNS_LOG_DIR="$preflight" KNS_WEB_ROOT="$candidate/web" DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
    sh -c 'cd "$1" && exec "$2"' _ "$preflight" "$candidate/server/KnS.Server" >"$preflight/output.log" 2>&1 &
preflight_pid=$!
if healthy "http://127.0.0.1:$preflight_port/health" && curl -fs -o /dev/null "http://127.0.0.1:$preflight_port/"; then
    preflight_ok=true
else
    preflight_ok=false
fi
kill "$preflight_pid" 2>/dev/null || true
wait "$preflight_pid" 2>/dev/null || true
if [[ $preflight_ok != true ]]; then
    tail -30 "$preflight/output.log" >&2
    rm -rf -- "$preflight" "$candidate"
    echo 'Preflight failed; nothing changed.' >&2
    exit 1
fi
rm -rf -- "$preflight"

previous=/opt/kns.previous
rm -rf -- "$previous"
systemctl stop kns-server 2>/dev/null || true
[[ -d /opt/kns ]] && mv /opt/kns "$previous"
mv "$candidate" /opt/kns
install -o root -g root -m 644 deploy/kns-server.service /etc/systemd/system/kns-server.service
systemctl daemon-reload
systemctl enable --quiet kns-server
systemctl start kns-server
if ! healthy "http://$probe_host:$port/health"; then
    journalctl -u kns-server -n 30 --no-pager >&2 || true
    if [[ -d $previous ]]; then
        systemctl stop kns-server || true
        rm -rf -- /opt/kns
        mv "$previous" /opt/kns
        systemctl start kns-server || true
        echo 'The new server did not start; the previous version is running again. The database was not changed.' >&2
    fi
    exit 1
fi
rm -rf -- "$previous"
echo "KnS server installed and healthy on $bind:$port (database $db)."

if [[ $reset == true ]]; then
    backup=/var/lib/kns/progress-reset-$(date -u +%Y%m%dT%H%M%SZ)
    install -d -o kns -g kns -m 700 "$backup"
    systemctl stop kns-server
    if ! python3 /opt/kns/tools/reset_progress.py --db "$db" --apply --backup "$backup/kns.sqlite" > "$backup/report.json"; then
        systemctl start kns-server || true
        echo "Progress reset failed; the database is unchanged. Report: $backup/report.json" >&2
        exit 1
    fi
    chown -R kns:kns "$backup" "$db"* 2>/dev/null || true
    systemctl start kns-server
    healthy "http://$probe_host:$port/health"
    echo "Progress reset; backup and report in $backup."
fi
