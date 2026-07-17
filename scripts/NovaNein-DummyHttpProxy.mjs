import fs from 'node:fs';
import http from 'node:http';
import https from 'node:https';

const configPath = process.env.NOVANEIN_DUMMY_HTTP_CONFIG || 'C:\\ProgramData\\NovaNein\\Server\\dummy-http-proxy\\proxy-config.json';
const config = JSON.parse(fs.readFileSync(configPath, 'utf8').replace(/^\uFEFF/, ''));
const listenAddress = config.listenAddress || '0.0.0.0';
const listenPort = Number(config.listenPort || 5188);
const upstream = new URL(config.upstream || 'https://127.0.0.1:5189');
const configuredNetworks = Array.isArray(config.allowedNetworks)
  ? config.allowedNetworks
  : [config.allowedNetwork || '192.0.2.0/24'];

if (upstream.protocol !== 'https:') throw new Error('Der Dummy-Proxy darf nur auf HTTPS weiterleiten.');
if (!Number.isInteger(listenPort) || listenPort < 1 || listenPort > 65535) throw new Error('Ungültiger HTTP-Port.');
const pfx = fs.readFileSync(config.clientPfxPath);
const ca = config.caPath ? fs.readFileSync(config.caPath) : undefined;

const agent = new https.Agent({
  ca,
  pfx,
  passphrase: config.clientPfxPassword || '',
  rejectUnauthorized: true,
});

function ipv4ToInteger(address) {
  const octets = address.split('.');
  if (octets.length !== 4) return null;

  let value = 0;
  for (const octet of octets) {
    if (!/^\d{1,3}$/.test(octet)) return null;
    const number = Number(octet);
    if (number < 0 || number > 255) return null;
    value = ((value << 8) | number) >>> 0;
  }
  return value;
}

function parseNetwork(value) {
  // Compatibility with the original prefix-based configuration.
  const cidr = value.endsWith('.') ? `${value}0/24` : value;
  const [address, prefixText = '32'] = cidr.split('/');
  const ip = ipv4ToInteger(address);
  const prefix = Number(prefixText);
  if (ip === null || !Number.isInteger(prefix) || prefix < 0 || prefix > 32) {
    throw new Error(`Ungültiges zugelassenes Netzwerk: ${value}`);
  }

  const mask = prefix === 0 ? 0 : (0xffffffff << (32 - prefix)) >>> 0;
  return { network: ip & mask, mask };
}

const allowedNetworks = configuredNetworks.map(parseNetwork);

const hopByHopHeaders = new Set([
  'connection', 'keep-alive', 'proxy-authenticate', 'proxy-authorization',
  'te', 'trailer', 'transfer-encoding', 'upgrade',
]);

function clientAddress(request) {
  return (request.socket.remoteAddress || '').replace(/^::ffff:/, '');
}

function isAllowed(request) {
  const address = clientAddress(request);
  if (address === '127.0.0.1') return true;
  const ip = ipv4ToInteger(address);
  return ip !== null && allowedNetworks.some(({ network, mask }) => (ip & mask) === network);
}

function forwardedHeaders(request) {
  const headers = {};
  for (const [name, value] of Object.entries(request.headers)) {
    if (!hopByHopHeaders.has(name.toLowerCase()) && value !== undefined) headers[name] = value;
  }
  headers.host = upstream.host;
  headers['x-forwarded-for'] = clientAddress(request);
  headers['x-forwarded-host'] = request.headers.host || `${listenAddress}:${listenPort}`;
  headers['x-forwarded-proto'] = 'http';
  return headers;
}

function rewriteSetCookie(value) {
  return value
    .replace(/;\s*Secure/gi, '')
    .replace(/SameSite=None/gi, 'SameSite=Lax');
}

function responseHeaders(headers, request) {
  const result = {};
  const publicOrigin = `http://${request.headers.host || `${listenAddress}:${listenPort}`}`;
  for (const [name, value] of Object.entries(headers)) {
    const lowerName = name.toLowerCase();
    if (lowerName === 'strict-transport-security') {
      continue;
    } else if (lowerName === 'set-cookie' && Array.isArray(value)) {
      result[name] = value.map(rewriteSetCookie);
    } else if (lowerName === 'location' && typeof value === 'string') {
      result[name] = value.replace(`${upstream.protocol}//${upstream.host}`, publicOrigin);
    } else {
      result[name] = value;
    }
  }
  return result;
}

const server = http.createServer((request, response) => {
  if (!isAllowed(request)) {
    response.writeHead(403, { 'content-type': 'text/plain; charset=utf-8' });
    response.end('Nur das lokale Dummy-Netz ist zugelassen.');
    return;
  }

  const proxyRequest = https.request({
    hostname: upstream.hostname,
    port: upstream.port || 443,
    path: request.url,
    method: request.method,
    headers: forwardedHeaders(request),
    agent,
  }, (proxyResponse) => {
    response.writeHead(proxyResponse.statusCode || 502, responseHeaders(proxyResponse.headers, request));
    proxyResponse.pipe(response);
  });

  proxyRequest.on('error', (error) => {
    if (!response.headersSent) response.writeHead(502, { 'content-type': 'text/plain; charset=utf-8' });
    response.end(`NovaNein-Dummy-Proxy: Upstream nicht erreichbar (${error.code || 'Fehler'}).`);
  });
  request.pipe(proxyRequest);
});

server.on('clientError', (error, socket) => socket.end(`HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n`));
server.listen(listenPort, listenAddress, () => {
  console.log(`NovaNein-Dummy-HTTP-Proxy hört auf http://${listenAddress}:${listenPort}/`);
});
