resource "google_dns_managed_zone" "root" {
  name        = "web-game-zone"
  dns_name    = "1bit.kr."
  description = "1bit.kr 루트 도메인"
}

resource "google_compute_global_address" "ip" {
  name = "web-game-static-ip"
}

resource "google_compute_managed_ssl_certificate" "cert" {
  name = "web-game-cert"

  managed {
    domains = ["1bit.kr."]
  }
}

resource "google_dns_record_set" "a" {
  name         = google_dns_managed_zone.root.dns_name
  managed_zone = google_dns_managed_zone.root.name
  type         = "A"
  ttl          = 300
  rrdatas      = [google_compute_global_address.ip.address]
}

#Cloudflare Worker 가 게임 원본으로 부르는 호스트. 1bit.kr 루트는 Worker 가 포트폴리오(GitHub Pages)로 돌리므로
#게임 LB 는 이 이름으로 따로 인증서를 받는다. A 레코드는 Cloudflare DNS 에 DNS only(회색)로 둔다.
resource "google_compute_managed_ssl_certificate" "origin" {
  name = "web-game-origin-cert"

  managed {
    domains = ["game-origin.1bit.kr."]
  }
}
