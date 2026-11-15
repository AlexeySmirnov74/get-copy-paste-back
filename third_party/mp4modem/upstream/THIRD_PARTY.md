# Third-party software

| Component | License | Purpose |
|---|---|---|
| FastAPI | MIT | Local prototype HTTP API |
| HTTPX | BSD-3-Clause | HTTP API tests |
| NumPy | BSD-3-Clause | YUV frame arrays and measurements |
| Pillow | HPND | Cover rendering and image fixtures |
| python-multipart | Apache-2.0 | Multipart uploads for the optional Web API |
| Mediabunny | MPL-2.0 | Browser MP4 mux/demux and WebCodecs integration |
| reedsolo | Public Domain | Python Reed-Solomon error correction and interoperability reference |
| TypeScript | Apache-2.0 | Type checking and browser package builds |
| Uvicorn | BSD-3-Clause | Local ASGI server |
| FFmpeg | LGPL/GPL build-dependent | H.264 encode/decode and inspection |

An FFmpeg build with libx264 commonly enables GPL components. Distributed binaries must be
reviewed separately; this repository invokes the executable installed by the user.
