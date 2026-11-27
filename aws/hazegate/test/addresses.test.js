'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { isBlockedAddress, isBadPort, parseIPv6 } = require('../function/src/lib/addresses');

test('private, loopback and Azure platform IPv4 addresses are blocked', () => {
  for (const address of ['127.0.0.1', '10.1.2.3', '172.16.0.1', '172.31.255.255', '192.168.1.1', '169.254.169.254',
    '168.63.129.16', '0.0.0.0', '100.64.0.1', '224.0.0.1', '255.255.255.255', '198.18.0.1']) {
    assert.equal(isBlockedAddress(address), true, address);
  }
});

test('public IPv4 addresses are allowed', () => {
  for (const address of ['8.8.8.8', '1.1.1.1', '172.32.0.1', '93.184.216.34', '168.63.129.17', '100.128.0.1']) {
    assert.equal(isBlockedAddress(address), false, address);
  }
});

test('IPv6 loopback, local and IPv4-carrying private addresses are blocked', () => {
  for (const address of ['::1', '::', 'fe80::1', 'fc00::1', 'fd12:3456::1', 'ff02::1', '::ffff:127.0.0.1',
    '::ffff:7f00:1', '64:ff9b::a9fe:a9fe', '2002:c0a8:0101::1', '2001:db8::1', '[::1]', 'fe80::1%eth0', 'not-an-ip']) {
    assert.equal(isBlockedAddress(address), true, address);
  }
});

test('public IPv6 addresses are allowed', () => {
  for (const address of ['2606:4700:4700::1111', '2a00:1450:4001:82a::200e', '::ffff:8.8.8.8', '64:ff9b::808:808']) {
    assert.equal(isBlockedAddress(address), false, address);
  }
});

test('IPv6 addresses are parsed into eight groups', () => {
  assert.deepEqual(parseIPv6('::1'), [0, 0, 0, 0, 0, 0, 0, 1]);
  assert.deepEqual(parseIPv6('2001:db8::8:800:200c:417a'), [0x2001, 0xdb8, 0, 0, 8, 0x800, 0x200c, 0x417a]);
  assert.deepEqual(parseIPv6('::ffff:1.2.3.4'), [0, 0, 0, 0, 0, 0xffff, 0x102, 0x304]);
  assert.equal(parseIPv6('1.2.3.4'), null);
});

test('ports browsers refuse are refused', () => {
  assert.equal(isBadPort(25), true);
  assert.equal(isBadPort(22), true);
  assert.equal(isBadPort(443), false);
  assert.equal(isBadPort(8080), false);
});
