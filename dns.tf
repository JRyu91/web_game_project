# 게임 LB 고정 IP. k8s/ingress.yaml 이 이름(web-game-static-ip)으로 붙잡는다.
resource "google_compute_global_address" "ip" {
  name = "web-game-static-ip"
}

# 1bit.kr DNS 는 Cloudflare(DNS only). 게임 API 는 game-origin.1bit.kr → 이 IP 로 A 레코드를 둔다.
# 루트(1bit.kr)는 GitHub Pages 라 여기서 인증서를 받지 않는다.
resource "google_compute_managed_ssl_certificate" "origin" {
  name = "web-game-origin-cert"

  managed {
    domains = ["game-origin.1bit.kr."]
  }
}
