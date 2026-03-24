#!/bin/bash
set -e
dnf install -y postgresql15 > /dev/null 2>&1
cp /usr/bin/psql /out/bin/
chmod +x /out/bin/psql
for lib in $(ldd /usr/bin/psql | grep "=> /" | awk "{print \$3}"); do
    cp "$lib" /out/lib/ 2>/dev/null || true
done
psql --version
echo "Done"
