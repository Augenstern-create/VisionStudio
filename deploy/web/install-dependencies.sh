#!/usr/bin/env bash
set -euo pipefail
sudo apt-get update
sudo apt-get install -y \
  libgeotiff5 liblapack3 libblas3 libopenjp2-7 \
  libgstreamer-plugins-base1.0-0 libavcodec60 libavformat60 libavutil58 libswscale7 \
  libgtk-3-0t64 libfontconfig1 libx11-6 libice6 libsm6 libxext6 \
  libgl1 libegl1 libgbm1 libinput10 nginx
