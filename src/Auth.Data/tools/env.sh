# Source me: exports the ff-db owner + app-role connection strings for <db> without printing them.
# (ff-db.sh writes CONNECTION_STRING unquoted, so it is read with sed rather than sourced.)
_db=${1:-ff_sse}
set -a; . "/tmp/ff-db/${_db}.env"; set +a
export CONNECTION_STRING="$(sed -n 's/^CONNECTION_STRING=//p' "/tmp/ff-db/${_db}.env")"
export ADMIN_API_CONNECTION_STRING="$(sed -n 's/^ADMIN_API_CONNECTION_STRING=//p' "/tmp/ff-db/${_db}_roles.env" | tr -d '"')"
export LOGIN_CONNECTION_STRING="$(sed -n 's/^LOGIN_CONNECTION_STRING=//p' "/tmp/ff-db/${_db}_roles.env" | tr -d '"')"
unset _db
