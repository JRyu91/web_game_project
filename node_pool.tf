# ── core 노드풀 ──────────────────────────────────────────────
# 온디맨드 고정 1대. redis·KEDA 등 죽으면 안 되는 파드 전용. 스팟이 다 날아가도 여기가 버팀.
resource "google_container_node_pool" "web_game_core_pool" {
  name     = "web-game-core"
  location = var.zone
  cluster  = google_container_cluster.web_game_cluster.name

  autoscaling {
    min_node_count = 1
    max_node_count = 2 # 평소엔 1대. 스팟이 다 선점당해 채널 파드가 몰릴 때만 잠깐 2대
  }

  node_config {
    service_account = google_service_account.default.email
    machine_type    = "e2-standard-2" # 2vCPU/8GB. 시스템+redis+KEDA+채널 2~3개까지 감당
    disk_size_gb    = 30
    disk_type       = "pd-standard"
    # 온디맨드 유지 — 안 죽는 게 목적
    oauth_scopes = [
      "https://www.googleapis.com/auth/cloud-platform"
    ]
    tags           = ["gke-node"]
    resource_labels = { pool = "core" }
    labels          = { pool = "core" } # k8s 쪽에서 nodeSelector 로 이 라벨을 본다
  }
}

# ── spot 노드풀 ──────────────────────────────────────────────
# KEDA 버스트용 값싼 스팟. 놀면 0대. 채널 파드만 올라가서 선점당해도 core 가 받아줌.
resource "google_container_node_pool" "web_game_node_pool" {
  name     = "web-game-node-pool"
  location = var.zone
  cluster  = google_container_cluster.web_game_cluster.name

  autoscaling {
    min_node_count = 0 # 접속자 없으면 스팟 노드는 0대 = 공짜
    max_node_count = 1 # CPUS_ALL_REGIONS 쿼터 12(무료 체험). core 최대 4 + spot 4 + 업그레이드 서지 여유. 채널 파드는 100m 라 1대로 충분
  }

  node_config {
    service_account = google_service_account.default.email
    machine_type    = "e2-standard-4"
    disk_size_gb    = 30
    disk_type       = "pd-standard"
    spot            = true # GKE 가 이 노드에 cloud.google.com/gke-spot taint 를 자동으로 박는다
    oauth_scopes = [
      "https://www.googleapis.com/auth/cloud-platform"
    ]
    tags           = ["gke-node"]
    resource_labels = { pool = "spot" }
    labels          = { pool = "spot" }
  }
}
