#!/bin/sh
set -e
touch /var/lib/bundler-deb-fixture-postinst.ran 2>/dev/null || touch /tmp/bundler-deb-fixture-postinst.ran
