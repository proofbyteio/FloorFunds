# Source me: exports the ff-db owner + app-role connection strings for <db> without printing them.
# ff-db.sh (md5 7f90790f, TLD 8:20) now single-quotes every value, so sourcing gives the full CONNECTION_STRING.
# Files written by the old ff-db.sh (823296e0) are unquoted and cut at the first ';' when sourced: the sed
# fallback below is kept for those until every env file is regenerated, then it can go (build log).
_db=${1:-ff_sse}
set -a; . "/tmp/ff-db/${_db}.env"; set +a
if ! grep -q "^CONNECTION_STRING='" "/tmp/ff-db/${_db}.env"; then
  export CONNECTION_STRING="$(sed -n 's/^CONNECTION_STRING=//p' "/tmp/ff-db/${_db}.env")"
fi
export ADMIN_API_CONNECTION_STRING="$(sed -n 's/^ADMIN_API_CONNECTION_STRING=//p' "/tmp/ff-db/${_db}_roles.env" | tr -d '"')"
export LOGIN_CONNECTION_STRING="$(sed -n 's/^LOGIN_CONNECTION_STRING=//p' "/tmp/ff-db/${_db}_roles.env" | tr -d '"')"
unset _db
