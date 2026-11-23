#include <enet/enet.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#define API __declspec(dllexport)
typedef struct { ENetHost *host; ENetPeer *peer; } ZeusHost;

API int zeus_init(void) { return enet_initialize(); }
API void *zeus_open(const char *bind_ip, uint16_t port, int outgoing_prefix, int incoming_prefix) {
    ENetAddress address;
    ZeusHost *z = (ZeusHost *)calloc(1, sizeof(ZeusHost));
    if (!z) return NULL;
    address.port = port;
    if (enet_address_set_host_ip(&address, bind_ip) != 0) { free(z); return NULL; }
    /* A redirect may reconnect before the retiring client closes its socket.
       Spare handshake slots prevent that old connection from blocking login. */
    z->host = enet_host_create(&address, incoming_prefix == 2 ? 8 : 1, 2, 0, 0);
    if (!z->host) { free(z); return NULL; }
    z->host->maximumPacketSize = 4 * 1024 * 1024;
    z->host->maximumWaitingData = 8 * 1024 * 1024;
    z->host->zeusOutgoingPrefix = outgoing_prefix != 0;
    z->host->zeusIncomingPrefix = incoming_prefix;
    z->host->checksum = enet_crc32;
    if (enet_host_compress_with_range_coder(z->host) != 0) {
        enet_host_destroy(z->host); free(z); return NULL;
    }
    return z;
}
API int zeus_connect(ZeusHost *z, const char *ip, uint16_t port) {
    ENetAddress address;
    address.port = port;
    if (!z || z->peer || enet_address_set_host_ip(&address, ip) != 0) return -1;
    z->peer = enet_host_connect(z->host, &address, 2, 0);
    if (!z->peer) return -1;
    enet_peer_timeout(z->peer, 8, 5000, 10000);
    return 0;
}
/* Copy event payload before destroying the ENet packet. No native pointers escape. */
API int zeus_poll(ZeusHost *z, unsigned char *buffer, int capacity,
    int *type, int *length, int *channel, int *flags) {
    ENetEvent event;
    if (!z || !buffer || capacity < 0) return -1;
    int result;
    for (int skipped = 0; ; ++skipped) {
        result = enet_host_service(z->host, &event, 0);
        if (result <= 0) return result;
        if (event.type == ENET_EVENT_TYPE_CONNECT) {
            /* A single local client is supported: the new CONNECT proves every
               previously established connection is retiring (redirect, cancelled
               attempt, client restart). Release them all so stale peers can
               never exhaust handshake slots. Pending handshakes are kept. */
            for (ENetPeer *p = z->host->peers; p < &z->host->peers[z->host->peerCount]; ++p) {
                if (p == event.peer) continue;
                if (p->state == ENET_PEER_STATE_CONNECTED || p->state == ENET_PEER_STATE_DISCONNECT_LATER ||
                    p->state == ENET_PEER_STATE_DISCONNECTING || p->state == ENET_PEER_STATE_ACKNOWLEDGING_DISCONNECT ||
                    p->state == ENET_PEER_STATE_ZOMBIE) {
                    enet_peer_reset_queues(p);
                    enet_peer_reset(p);
                }
            }
            z->peer = event.peer;
            break;
        }
        if (event.peer == z->peer) break;
        if (event.type == ENET_EVENT_TYPE_RECEIVE) enet_packet_destroy(event.packet);
        if (skipped >= 63) return 0;
    }
    *type = event.type; *length = 0; *channel = event.channelID; *flags = 0;
    if (event.type == ENET_EVENT_TYPE_CONNECT) {
        enet_peer_timeout(z->peer, 8, 5000, 10000);
    } else if (event.type == ENET_EVENT_TYPE_DISCONNECT) z->peer = NULL;
    else if (event.type == ENET_EVENT_TYPE_RECEIVE) {
        if (event.packet->dataLength > (size_t)capacity) {
            enet_packet_destroy(event.packet); return -2;
        }
        *length = (int)event.packet->dataLength;
        *flags = event.packet->flags & (ENET_PACKET_FLAG_RELIABLE |
            ENET_PACKET_FLAG_UNSEQUENCED | ENET_PACKET_FLAG_UNRELIABLE_FRAGMENT);
        memcpy(buffer, event.packet->data, event.packet->dataLength);
        enet_packet_destroy(event.packet);
    }
    return 1;
}
API int zeus_send(ZeusHost *z, const void *data, int length, int channel, int flags) {
    if (!z || !z->peer || length < 0 || length > 4*1024*1024 || channel < 0 || channel > 1) return -1;
    ENetPacket *packet = enet_packet_create(data, length, flags & 11);
    if (!packet) return -1;
    if (enet_peer_send(z->peer, (enet_uint8)channel, packet) != 0) {
        enet_packet_destroy(packet); return -1;
    }
    return 0;
}
API void zeus_flush(ZeusHost *z) { if(z) enet_host_flush(z->host); }
API void zeus_reset(ZeusHost *z) {
    if (z && z->peer) { enet_peer_disconnect_now(z->peer, 0); z->peer = NULL; }
}
API unsigned int zeus_rtt(ZeusHost *z) { return z && z->peer ? z->peer->roundTripTime : 0; }
API int zeus_connected(ZeusHost *z) { return z && z->peer && z->peer->state == ENET_PEER_STATE_CONNECTED; }
API uint16_t zeus_port(ZeusHost *z) { return z ? z->host->address.port : 0; }
API unsigned int zeus_received_datagrams(ZeusHost *z) { return z ? z->host->totalReceivedPackets : 0; }
API void zeus_checksum(ZeusHost *z, int enabled) { if(z) z->host->checksum = enabled ? enet_crc32 : NULL; }
API int zeus_receive_format(ZeusHost *z) { return z && z->host->zeusDetectedPrefix >= 0 ? (z->host->zeusDetectedPrefix + 1) * 10 + (z->host->checksum != NULL) : 0; }
API void zeus_close(ZeusHost *z) {
    if (z) { zeus_reset(z); enet_host_destroy(z->host); free(z); }
}
