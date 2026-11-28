'use strict';

// Which addresses HazeGate may connect to. HazeGate downloads whatever address it is sent, so without these checks it
// could be used to reach machines that are not on the public internet: Azure's own services (the instance metadata
// endpoint, the platform's 168.63.129.16), the function's local network, or this machine itself. Every address a host
// name resolves to is checked, for every request and every redirect (see fetcher.js).

const net = require('node:net');

// Ports browsers refuse to connect to (the "bad ports" of the Fetch standard), so HazeGate cannot be used to talk to
// mail, file, chat and other servers that are not web servers.
const BAD_PORTS = new Set([
  0, 1, 7, 9, 11, 13, 15, 17, 19, 20, 21, 22, 23, 25, 37, 42, 43, 53, 69, 77, 79, 87, 95, 101, 102, 103, 104, 109, 110,
  111, 113, 115, 117, 119, 123, 135, 137, 139, 143, 161, 179, 389, 427, 465, 512, 513, 514, 515, 526, 530, 531, 532,
  540, 548, 554, 556, 563, 587, 601, 636, 989, 990, 993, 995, 1719, 1720, 1723, 2049, 3659, 4045, 4190, 5060, 5061,
  6000, 6566, 6665, 6666, 6667, 6668, 6669, 6679, 6697, 10080,
]);

// IPv4 ranges that are not the public internet: [first address, prefix length].
const BLOCKED_IPV4 = [
  ['0.0.0.0', 8], // "this network"
  ['10.0.0.0', 8], // private
  ['100.64.0.0', 10], // carrier-grade NAT
  ['127.0.0.0', 8], // loopback
  ['169.254.0.0', 16], // link-local, including the 169.254.169.254 metadata endpoint
  ['172.16.0.0', 12], // private
  ['192.0.0.0', 24], // IETF protocol assignments
  ['192.0.2.0', 24], // documentation
  ['192.88.99.0', 24], // 6to4 relays
  ['192.168.0.0', 16], // private
  ['198.18.0.0', 15], // benchmarking
  ['198.51.100.0', 24], // documentation
  ['203.0.113.0', 24], // documentation
  ['224.0.0.0', 4], // multicast
  ['240.0.0.0', 4], // reserved and broadcast
  ['168.63.129.16', 32], // Azure platform (DNS, health probes, wire server)
].map(([address, prefix]) => [ipv4ToNumber(address), prefix]);

function ipv4ToNumber(address) {
  const parts = address.split('.').map(Number);
  return (((parts[0] << 24) >>> 0) + (parts[1] << 16) + (parts[2] << 8) + parts[3]) >>> 0;
}

function isBlockedIPv4(address) {
  const value = ipv4ToNumber(address);
  return BLOCKED_IPV4.some(([first, prefix]) => {
    const mask = prefix === 0 ? 0 : (0xffffffff << (32 - prefix)) >>> 0;
    return ((value & mask) >>> 0) === ((first & mask) >>> 0);
  });
}

/** The eight 16-bit groups of an IPv6 address, or null when it is not one. */
function parseIPv6(address) {
  let text = String(address).trim().toLowerCase();
  if (text.startsWith('[') && text.endsWith(']')) {
    text = text.slice(1, -1);
  }

  const zone = text.indexOf('%');
  if (zone >= 0) {
    text = text.slice(0, zone);
  }

  if (net.isIPv6(text) === false) {
    return null;
  }

  // An IPv4 address at the end ("::ffff:127.0.0.1") becomes two groups.
  const lastColon = text.lastIndexOf(':');
  const tail = text.slice(lastColon + 1);
  if (tail.includes('.')) {
    const value = ipv4ToNumber(tail);
    text = `${text.slice(0, lastColon + 1)}${(value >>> 16).toString(16)}:${(value & 0xffff).toString(16)}`;
  }

  const halves = text.split('::');
  const head = halves[0] ? halves[0].split(':') : [];
  const rest = halves.length > 1 && halves[1] ? halves[1].split(':') : [];
  const missing = 8 - head.length - rest.length;
  const groups = [...head, ...Array(halves.length > 1 ? missing : 0).fill('0'), ...rest].map((group) => parseInt(group, 16));
  return groups.length === 8 && groups.every((group) => Number.isInteger(group) && group >= 0 && group <= 0xffff) ? groups : null;
}

function groupsToIPv4(high, low) {
  return `${high >> 8}.${high & 0xff}.${low >> 8}.${low & 0xff}`;
}

function isBlockedIPv6(address) {
  const g = parseIPv6(address);
  if (g === null) {
    return true;
  }

  const allZero = (from, to) => g.slice(from, to).every((group) => group === 0);
  if (allZero(0, 8)) {
    return true; // :: (unspecified)
  }

  if (allZero(0, 7) && g[7] === 1) {
    return true; // ::1 (loopback)
  }

  // Addresses that carry an IPv4 address are judged by it: IPv4-mapped (::ffff:a.b.c.d), IPv4-compatible (::a.b.c.d),
  // NAT64 (64:ff9b::a.b.c.d) and 6to4 (2002:aabb:ccdd::).
  if (allZero(0, 5) && (g[5] === 0xffff || g[5] === 0)) {
    return isBlockedIPv4(groupsToIPv4(g[6], g[7]));
  }

  if (g[0] === 0x64 && g[1] === 0xff9b && allZero(2, 6)) {
    return isBlockedIPv4(groupsToIPv4(g[6], g[7]));
  }

  if (g[0] === 0x2002) {
    return isBlockedIPv4(groupsToIPv4(g[1], g[2]));
  }

  return (
    (g[0] === 0x64 && g[1] === 0xff9b && g[2] === 1) || // 64:ff9b:1::/48, local NAT64
    (g[0] === 0x100 && allZero(1, 4)) || // 100::/64, discard
    (g[0] === 0x2001 && g[1] < 0x200) || // 2001::/23, IETF protocol assignments (Teredo included)
    (g[0] === 0x2001 && g[1] === 0xdb8) || // 2001:db8::/32, documentation
    (g[0] & 0xfe00) === 0xfc00 || // fc00::/7, unique local
    (g[0] & 0xffc0) === 0xfe80 || // fe80::/10, link-local
    (g[0] & 0xffc0) === 0xfec0 || // fec0::/10, site-local
    (g[0] & 0xff00) === 0xff00 // ff00::/8, multicast
  );
}

/** True when HazeGate must not connect to this IP address (it is not on the public internet). */
function isBlockedAddress(address) {
  const text = String(address).trim().replace(/^\[|\]$/g, '');
  if (net.isIPv4(text)) {
    return isBlockedIPv4(text);
  }

  return isBlockedIPv6(text);
}

/** True for ports browsers never connect to. */
function isBadPort(port) {
  return BAD_PORTS.has(Number(port));
}

module.exports = { isBlockedAddress, isBadPort, parseIPv6 };
