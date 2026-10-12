#!/bin/bash
# usage: stress.sh <procs> <seconds>
for i in $(seq 1 $1); do timeout $2 python -I -c "while True: pass" & done; wait
