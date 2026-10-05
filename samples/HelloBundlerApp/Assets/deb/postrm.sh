#!/bin/sh
set -e
# 示例 postrm：清除运行时状态目录。
rm -rf /var/lib/hello-deb-app
