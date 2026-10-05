const test = require('node:test');
const assert = require('node:assert/strict');
const { createTabController } = require('../app/public/tabs.js');

test('默认显示自己战绩；进入新对局只切换一次，尊重手动切回', () => {
  const tabs = createTabController();
  assert.equal(tabs.sync(false), 'history');
  assert.equal(tabs.sync(true), 'match');
  tabs.choose('history');
  assert.equal(tabs.sync(true), 'history');
  assert.equal(tabs.sync(true), 'history');
  assert.equal(tabs.sync(false), 'history');
  assert.equal(tabs.sync(true), 'match');
});

test('手动切换对局页不被大厅刷新覆盖；结束对局回到自己的战绩', () => {
  const tabs = createTabController();
  tabs.choose('match');
  assert.equal(tabs.sync(false), 'match');
  tabs.sync(true);
  assert.equal(tabs.sync(false), 'history');
});
