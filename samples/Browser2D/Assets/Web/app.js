'use strict';
const $ = (id) => document.getElementById(id);

for (let i = 1; i <= 30; i++) {
  const row = document.createElement('div');
  row.textContent = 'Scrollable row ' + i;
  $('list').appendChild(row);
}

function log(text) {
  const item = document.createElement('li');
  item.textContent = text;
  $('log').prepend(item);
}

setInterval(() => { $('clock').textContent = new Date().toLocaleTimeString(); }, 250);

function connect() {
  if (!window.gameApi) {
    return false;
  }

  $('bridge').textContent = 'bridge: connected';
  $('bridge').classList.add('on');
  gameApi.onMessage((name, data) => {
    if (name === 'score') {
      $('score').textContent = 'Score: ' + data.clicks;
    }
  });
  gameApi.log('page ready');
  return true;
}

if (!connect()) {
  window.addEventListener('gameapiready', connect, { once: true });
}

$('click').addEventListener('click', () => {
  if (window.gameApi) {
    gameApi.sendMessage('clicked', 'button');
  }
});

$('stats').addEventListener('click', async () => {
  try {
    const stats = await gameApi.request('stats');
    $('answer').textContent = JSON.stringify(stats);
  } catch (e) {
    $('answer').textContent = 'error: ' + e.message;
  }
});

$('form').addEventListener('submit', (e) => {
  e.preventDefault();
  const value = $('name').value;
  log('submitted: ' + value);
  if (window.gameApi) {
    gameApi.sendMessage('submitted', { text: value });
  }
  $('name').value = '';
});

// Page audio plays through the game's AudioMixer ("Browser" group), not straight to the speakers.
$('tone').addEventListener('click', () => {
  const context = new AudioContext();
  const oscillator = context.createOscillator();
  const gain = context.createGain();
  oscillator.frequency.value = 440;
  gain.gain.setValueAtTime(0.3, context.currentTime);
  gain.gain.exponentialRampToValueAtTime(0.001, context.currentTime + 0.6);
  oscillator.connect(gain).connect(context.destination);
  oscillator.start();
  oscillator.stop(context.currentTime + 0.6);
  log('tone played');
});
