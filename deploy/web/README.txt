Vision Studio Web 1.0.0 - Ubuntu 24.04 x64

1. Install native dependencies:
   sudo ./install-dependencies.sh

2. Copy the extracted directory to /opt/visionstudio-web.

3. Create configuration and a random access token:
   cp .env.example .env
   sed -i "s/replace-with-a-long-random-token/$(openssl rand -hex 32)/" .env
   chmod 600 .env

4. Test directly on localhost:
   ./start.sh
   Open http://127.0.0.1:5080 and enter the token from .env.

5. Optional production service and reverse proxy:
   sudo cp visionstudio-web.service /etc/systemd/system/
   sudo systemctl daemon-reload
   sudo systemctl enable --now visionstudio-web
   Copy nginx.conf.example to /etc/nginx/sites-available, update server_name,
   enable the site, add HTTPS with your certificate/ACME tool, then reload nginx.

Project data is stored in /var/lib/visionstudio-web. Back up that directory.
Never expose the HTTP endpoint directly to an untrusted network; use HTTPS.
