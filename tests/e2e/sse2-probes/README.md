# SSE2 black-box focus probes (SI1, 3feb66c)
Not part of the xUnit suite. Python 3 with `cryptography` (own venv, e.g. /tmp/sse2-work/venv). prelude.py is derived from SSE's
harness (drive.py, handoff.py, findings.py, step4.py, reenroll.py at 3feb66c) and reads passwords and generated secrets from the
stand-in fixtures, never from literals. Paths at the top of prelude.py point at the SSE2 export /tmp/sse2-si1-3feb66c and at the
stand-in's own state dir /tmp/sse-d0/standin-$STANDIN_PORT_BASE (hard-coded by the stand-in, finding G8).

    tools/check-ports.sh mine /tmp/sse2-si1-3feb66c/pid 18090 18091 18092
    STANDIN_PORT_BASE=18090 python probe.py      # 135/147 at 3feb66c (fails = findings C0, C9, C10, K-huge, K-val, ADM)
    STANDIN_PORT_BASE=18090 python probe-k.py    # 11/14 at 3feb66c (fails = K-DoS boundary, finding G1)

Each script refuses to run unless the ports are owned by the pid in the stand-in pidfile, and resets the stand-in at the end.
