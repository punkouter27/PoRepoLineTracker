// Effects layer: everything decorative that is not the backdrop (backdrop.js). Synthesised sound,
// one WebGL2 particle overlay above the page, the SVG filters the stylesheets reference, and the
// pointer-driven surfaces (.u-glass highlight, .u-holo tilt). Owner-approved 2026-10-23, see the
// SPEC delta "GFX & sound".
//
// Nothing here is on a data path. Every entry point is wrapped, and a failure (or this file never
// loading) leaves the page exactly as it was. Callers need to know none of the switches:
//   - sound plays only when the header toggle stored localStorage 'sound' = 'on' (index.html);
//   - motion (particles, tilt, animated filters) never starts under prefers-reduced-motion;
//   - localStorage 'fx' = 'off', set by hand in devtools like 'backdrop', turns the visual half off.
(function () {
    'use strict';

    var noop = function () { };
    var visuals = !prefersReducedMotion(); // index.html's boot script, which runs before this deferred file
    try { if (localStorage.getItem('fx') === 'off') visuals = false; } catch (e) { }

    function split(list) {
        return (list || '').split(',').filter(function (s) { return s; });
    }

    // FNV-1a. The same extension always lands on the same note and the same colour.
    function hash(text) {
        var h = 2166136261;
        for (var i = 0; i < text.length; i++) { h ^= text.charCodeAt(i); h = Math.imul(h, 16777619); }
        return h >>> 0;
    }

    function hsl(h, s, l) {
        var k = function (n) { return (n + h / 30) % 12; };
        var a = s * Math.min(l, 1 - l);
        var f = function (n) { return l - a * Math.max(-1, Math.min(k(n) - 3, 9 - k(n), 1)); };
        return [f(0), f(8), f(4)];
    }

    function colour(extension) { return hsl(hash(extension) % 360, 0.85, 0.62); }

    // The brand pair from app.css plus the status teal and a gold, for bursts with no file types.
    var BRAND = [[0.40, 0.31, 0.64], [0.75, 0.18, 0.46], [0.20, 0.76, 0.70], [1.0, 0.82, 0.25], [0.55, 0.45, 0.95]];

    function palette(extensions) {
        var list = split(extensions).slice(0, 4).map(colour);
        return list.length ? list.concat([[1, 0.85, 0.3]]) : BRAND;
    }

    // ── Sound ───────────────────────────────────────────────────────────────────────────────
    //
    // All synthesised: oscillators, filtered noise, and a convolver fed a generated impulse for
    // the reverb. A compressor sits in front of the speakers so cues that overlap (a bulk import
    // finishing several repositories at once) stay a level instead of clipping.
    var audio = null, master = null, wet = null, noiseBuffer = null;

    function context() {
        if (!soundOn()) return null;
        var Ctx = window.AudioContext || window.webkitAudioContext;
        if (!Ctx) return null;
        if (!audio) {
            audio = new Ctx();
            master = audio.createDynamicsCompressor();
            master.connect(audio.destination);
            wet = audio.createConvolver();
            wet.buffer = impulse(2.4);
            var send = audio.createGain();
            send.gain.value = 0.35;
            wet.connect(send);
            send.connect(master);
            noiseBuffer = audio.createBuffer(1, audio.sampleRate, audio.sampleRate);
            var data = noiseBuffer.getChannelData(0);
            for (var i = 0; i < data.length; i++) data[i] = Math.random() * 2 - 1;
        }
        if (audio.state === 'suspended') audio.resume().catch(noop);
        return audio;
    }

    // A synthetic room: two channels of noise decaying as a cube. Convolving with it is the whole reverb.
    function impulse(seconds) {
        var length = Math.floor(audio.sampleRate * seconds);
        var buffer = audio.createBuffer(2, length, audio.sampleRate);
        for (var c = 0; c < 2; c++) {
            var data = buffer.getChannelData(c);
            for (var i = 0; i < length; i++) data[i] = (Math.random() * 2 - 1) * Math.pow(1 - i / length, 3);
        }
        return buffer;
    }

    // Exponential ramps cannot start from or reach 0, and a note cut off at full gain clicks, so
    // every envelope fades in and out through a value just above it.
    function envelope(at, length, peak, attack) {
        var gain = audio.createGain();
        gain.gain.setValueAtTime(0.0001, at);
        gain.gain.exponentialRampToValueAtTime(peak, at + attack);
        gain.gain.exponentialRampToValueAtTime(0.0001, at + length);
        gain.connect(master);
        return gain;
    }

    function note(frequency, at, length, peak, type, reverb, glideTo) {
        var osc = audio.createOscillator();
        var gain = envelope(at, length, peak, 0.02);
        osc.type = type || 'sine';
        osc.frequency.setValueAtTime(frequency, at);
        if (glideTo) osc.frequency.exponentialRampToValueAtTime(glideTo, at + length);
        osc.connect(gain);
        if (reverb) gain.connect(wet);
        osc.start(at);
        osc.stop(at + length + 0.03);
    }

    function noise(at, length, peak, type, fromHz, toHz, q) {
        var source = audio.createBufferSource();
        var filter = audio.createBiquadFilter();
        var gain = envelope(at, length, peak, Math.min(0.04, length / 4));
        source.buffer = noiseBuffer;
        source.loop = true;
        filter.type = type;
        filter.Q.value = q || 1;
        filter.frequency.setValueAtTime(fromHz, at);
        filter.frequency.exponentialRampToValueAtTime(toHz, at + length);
        source.connect(filter);
        filter.connect(gain);
        source.start(at);
        source.stop(at + length + 0.03);
    }

    // Minor pentatonic from A3. Any two of its notes sit well together, which is what lets the data
    // choose the notes without ever choosing a wrong one.
    var PENTATONIC = [0, 3, 5, 7, 10];
    function degree(n) {
        n = Math.max(0, Math.round(n));
        return 220 * Math.pow(2, (PENTATONIC[n % 5] + 12 * Math.floor(n / 5)) / 12);
    }

    function sound(play) {
        try {
            var c = context();
            if (c) play(c.currentTime + 0.01);
        } catch (e) {
            // A cue that cannot play is not worth a console error, let alone a broken page.
        }
    }

    // kind: 'done' or 'failed'. With extensions ('cs,razor,js', most lines first) 'done' becomes
    // the repository's own chord: its top three file types pick the notes, arpeggiated, then held.
    window.playCue = function (kind, extensions) {
        sound(function (t) {
            if (kind === 'failed') {
                // A falling fifth on two oscillators 12 cents apart, which beat against each other.
                [293.66, 196].forEach(function (f, i) {
                    note(f, t + i * 0.18, 0.55, 0.07, 'triangle');
                    note(f * 1.007, t + i * 0.18, 0.55, 0.05, 'triangle');
                });
                return;
            }
            var list = split(extensions).slice(0, 3);
            if (!list.length) {
                note(659.25, t, 0.32, 0.12);
                note(880, t + 0.14, 0.32, 0.12);
                return;
            }
            var d = 5 + hash(list[0]) % 5;
            note(degree(d - 5), t, 1.6, 0.05, 'sine', true);
            list.forEach(function (extension, i) {
                if (i > 0) d += 2 + hash(extension) % 2;
                note(degree(d), t + i * 0.09, 1.4 - i * 0.1, 0.08, 'triangle', true);
            });
        });
    };

    // One soft blip per progress frame, pitched by lines per commit. Throttled: a fast analysis
    // pushes frames faster than a tick can ring.
    var lastTick = 0;
    function tick(lines, commits) {
        var now = performance.now();
        if (now - lastTick < 90) return;
        lastTick = now;
        var perCommit = lines / Math.max(1, commits);
        sound(function (t) { note(degree(10 + Math.min(9, Math.log2(1 + perCommit) / 1.6)), t, 0.1, 0.035, 'sine', true); });
    }

    // ── Particle overlay ───────────────────────────────────────────────────────────────────
    //
    // One fixed WebGL2 canvas above the page (pointer-events: none), created on first use. The
    // simulation runs on the CPU in flat typed arrays (a few thousand particles at most is well
    // inside a frame's budget). WebGL draws them as point sprites in one call: confetti as
    // flipping paper rectangles, everything else as glowing cores. The rAF loop runs only while
    // something is alive.
    var FLOW = 0, CONFETTI = 1, DUST = 2, EMBER = 3, SPARK = 4;
    var MAX = 12000, STRIDE = 9;
    var px = new Float32Array(MAX), py = new Float32Array(MAX), vx = new Float32Array(MAX), vy = new Float32Array(MAX);
    var life = new Float32Array(MAX), span = new Float32Array(MAX), size = new Float32Array(MAX);
    var cr = new Float32Array(MAX), cg = new Float32Array(MAX), cb = new Float32Array(MAX);
    var rot = new Float32Array(MAX), spin = new Float32Array(MAX), tx = new Float32Array(MAX), ty = new Float32Array(MAX);
    var kind = new Uint8Array(MAX), bounces = new Uint8Array(MAX);
    var vertices = new Float32Array(MAX * STRIDE);
    var count = 0, emitters = [], colliders = [], riverTarget = null;
    var canvas = null, gl = null, uniforms = null, buffer = null, running = false, last = 0, clock = 0, broken = false;

    var VERTEX = '#version 300 es\n' +
        'layout(location=0) in vec2 pos;layout(location=1) in float size;layout(location=2) in vec4 col;layout(location=3) in vec2 extra;' +
        'uniform vec2 res;uniform float dpr;out vec4 vCol;flat out float vKind;out float vRot;' +
        'void main(){vec2 c=pos/res*2.-1.;gl_Position=vec4(c.x,-c.y,0.,1.);gl_PointSize=size*dpr;vCol=col;vKind=extra.x;vRot=extra.y;}';

    // Confetti: a rectangle rotating in the sprite, its width scaled by |cos| of a second angle so
    // it reads as paper flipping in 3D, shaded darker on the back. Glows: a gaussian core and halo
    // with a white-hot centre. Premultiplied output with alpha 0 is additive blending through the
    // same blend function, so on a dark page sparks light up what is behind them. On a light page
    // that would be white on white, so there they composite normally.
    var FRAGMENT = '#version 300 es\n' +
        'precision mediump float;in vec4 vCol;flat in float vKind;in float vRot;uniform float additive;out vec4 o;' +
        'void main(){vec2 p=gl_PointCoord*2.-1.;' +
        'if(vKind==1.){float c=cos(vRot),s=sin(vRot);vec2 q=mat2(c,-s,s,c)*p;float flip=abs(cos(vRot*1.7));' +
        'if(abs(q.x)>.9*max(flip,.12)||abs(q.y)>.42)discard;o=vec4(vCol.rgb*(.6+.4*flip)*vCol.a,vCol.a);return;}' +
        'float d=dot(p,p);if(d>1.)discard;float a=vCol.a*(exp(-d*4.)+.35*exp(-d*14.));' +
        'vec3 rgb=mix(vCol.rgb,vec3(1.),exp(-d*20.)*.7);o=vec4(rgb*a,a*(1.-additive));}';

    function overlay() {
        if (gl) return true;
        if (broken || !visuals) return false;
        try {
            canvas = document.createElement('canvas');
            canvas.id = 'fx-overlay';
            canvas.setAttribute('aria-hidden', 'true');
            gl = canvas.getContext('webgl2', { premultipliedAlpha: true, antialias: false });
            if (!gl) throw new Error('no webgl2');

            var program = gl.createProgram();
            [[gl.VERTEX_SHADER, VERTEX], [gl.FRAGMENT_SHADER, FRAGMENT]].forEach(function (s) {
                var shader = gl.createShader(s[0]);
                gl.shaderSource(shader, s[1]);
                gl.compileShader(shader);
                gl.attachShader(program, shader);
            });
            gl.linkProgram(program);
            if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(program));
            gl.useProgram(program);
            uniforms = { res: gl.getUniformLocation(program, 'res'), dpr: gl.getUniformLocation(program, 'dpr'), additive: gl.getUniformLocation(program, 'additive') };

            buffer = gl.createBuffer();
            gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
            gl.bufferData(gl.ARRAY_BUFFER, vertices.byteLength, gl.DYNAMIC_DRAW);
            [[0, 2, 0], [1, 1, 2], [2, 4, 3], [3, 2, 7]].forEach(function (a) {
                gl.enableVertexAttribArray(a[0]);
                gl.vertexAttribPointer(a[0], a[1], gl.FLOAT, false, STRIDE * 4, a[2] * 4);
            });
            gl.enable(gl.BLEND);
            gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA);

            document.body.appendChild(canvas);
            resize();
            window.addEventListener('resize', resize);
            canvas.addEventListener('webglcontextlost', function () { broken = true; count = 0; emitters = []; gl = null; });
            return true;
        } catch (e) {
            broken = true;
            if (canvas) canvas.remove();
            gl = null;
            return false;
        }
    }

    function resize() {
        var dpr = Math.min(window.devicePixelRatio || 1, 2);
        canvas.width = Math.round(innerWidth * dpr);
        canvas.height = Math.round(innerHeight * dpr);
        gl.viewport(0, 0, canvas.width, canvas.height);
        gl.uniform2f(uniforms.res, innerWidth, innerHeight);
        gl.uniform1f(uniforms.dpr, dpr);
    }

    function spawn(k, x, y, velocityX, velocityY, lifetime, pointSize, rgb) {
        if (count >= MAX) return -1;
        var i = count++;
        kind[i] = k; px[i] = x; py[i] = y; vx[i] = velocityX; vy[i] = velocityY;
        life[i] = span[i] = lifetime; size[i] = pointSize;
        cr[i] = rgb[0]; cg[i] = rgb[1]; cb[i] = rgb[2];
        rot[i] = Math.random() * 6.283; spin[i] = (Math.random() - 0.5) * 14; bounces[i] = 0;
        return i;
    }

    function kill(i) {
        var j = --count;
        if (i === j) return;
        kind[i] = kind[j]; px[i] = px[j]; py[i] = py[j]; vx[i] = vx[j]; vy[i] = vy[j];
        life[i] = life[j]; span[i] = span[j]; size[i] = size[j]; cr[i] = cr[j]; cg[i] = cg[j]; cb[i] = cb[j];
        rot[i] = rot[j]; spin[i] = spin[j]; tx[i] = tx[j]; ty[i] = ty[j]; bounces[i] = bounces[j];
    }

    // Top edges of the cards on screen, captured when a burst starts: confetti lands on them.
    function captureColliders() {
        colliders = [];
        var cards = document.querySelectorAll('.rz-card, .kpi__surface');
        for (var i = 0; i < cards.length && colliders.length < 48; i++) {
            var r = cards[i].getBoundingClientRect();
            if (r.width > 0 && r.bottom > 0 && r.top < innerHeight) colliders.push([r.left, r.right, r.top]);
        }
    }

    function update(dt) {
        var arrived = 0;
        for (var i = count - 1; i >= 0; i--) {
            life[i] -= dt;
            if (life[i] <= 0) { kill(i); continue; }
            var k = kind[i], oldY = py[i];

            if (k === FLOW) {
                // Steering toward the target plus a swirl perpendicular to it, from a cheap
                // trigonometric flow field, so the stream braids instead of travelling in lines.
                var dx = tx[i] - px[i], dy = ty[i] - py[i], d = Math.sqrt(dx * dx + dy * dy) || 1;
                if (d < 16) { kill(i); arrived++; continue; }
                var swirl = Math.sin(px[i] * 0.011 + clock * 1.7) + Math.cos(py[i] * 0.013 - clock * 1.3);
                var speed = 340 + Math.min(d, 600) * 0.6;
                vx[i] += ((dx / d) * speed - vx[i]) * 3 * dt - (dy / d) * swirl * 420 * dt;
                vy[i] += ((dy / d) * speed - vy[i]) * 3 * dt + (dx / d) * swirl * 420 * dt;
            } else if (k === CONFETTI) {
                vy[i] += 1100 * dt;
                vx[i] *= Math.exp(-1.4 * dt);
                vy[i] *= Math.exp(-0.9 * dt);
                vx[i] += Math.sin(clock * 6 + i) * 120 * dt; // flutter
                rot[i] += spin[i] * dt;
            } else if (k === DUST) {
                vx[i] += (40 + Math.sin(py[i] * 0.03 + clock * 2) * 90) * dt;
                vy[i] -= 70 * dt;
                vx[i] *= Math.exp(-0.8 * dt);
                vy[i] *= Math.exp(-0.8 * dt);
                size[i] += 3 * dt;
            } else if (k === EMBER) {
                vy[i] -= 160 * dt; // buoyant
                vx[i] += Math.sin(clock * 5 + i * 1.7) * 90 * dt;
                vx[i] *= Math.exp(-1.2 * dt);
                vy[i] *= Math.exp(-1.0 * dt);
            } else {
                vy[i] += 260 * dt;
                vx[i] *= Math.exp(-1.6 * dt);
                vy[i] *= Math.exp(-1.6 * dt);
            }

            px[i] += vx[i] * dt;
            py[i] += vy[i] * dt;

            if (k === CONFETTI && vy[i] > 0) {
                var floor = innerHeight - 4;
                for (var c = 0; c < colliders.length; c++) {
                    var e = colliders[c];
                    if (oldY <= e[2] && py[i] >= e[2] && px[i] >= e[0] && px[i] <= e[1]) { floor = e[2]; break; }
                }
                if (py[i] >= floor) {
                    py[i] = floor;
                    vy[i] *= -0.3;
                    vx[i] *= 0.55;
                    spin[i] *= 0.4;
                    if (++bounces[i] >= 3) { vy[i] = 0; vx[i] = 0; life[i] = Math.min(life[i], 0.7); }
                }
            }
        }
        if (arrived && riverTarget) flash(riverTarget);
    }

    function alpha(i) {
        var t = life[i] / span[i];
        switch (kind[i]) {
            case FLOW: return Math.min(1, t * 4) * 0.9;
            case CONFETTI: return Math.min(1, life[i] * 1.6);
            case EMBER: return Math.min(1, t * 2) * (0.7 + 0.3 * Math.sin(clock * 30 + i));
            default: return t;
        }
    }

    function render() {
        gl.clearColor(0, 0, 0, 0);
        gl.clear(gl.COLOR_BUFFER_BIT);
        if (!count) return;
        gl.uniform1f(uniforms.additive, document.documentElement.getAttribute('data-theme') === 'dark' ? 1 : 0);
        for (var i = 0, o = 0; i < count; i++, o += STRIDE) {
            vertices[o] = px[i]; vertices[o + 1] = py[i]; vertices[o + 2] = size[i];
            vertices[o + 3] = cr[i]; vertices[o + 4] = cg[i]; vertices[o + 5] = cb[i]; vertices[o + 6] = alpha(i);
            vertices[o + 7] = kind[i]; vertices[o + 8] = rot[i];
        }
        gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
        gl.bufferSubData(gl.ARRAY_BUFFER, 0, vertices, 0, count * STRIDE);
        gl.drawArrays(gl.POINTS, 0, count);
    }

    function step(now) {
        if (!gl) { running = false; return; }
        var dt = Math.min(0.033, (now - last) / 1000);
        last = now;
        clock += dt;
        emitters = emitters.filter(function (emit) { return emit(dt) !== false; });
        update(dt);
        render();
        if (count || emitters.length) requestAnimationFrame(step);
        else running = false;
    }

    function run() {
        if (running || !gl) return;
        running = true;
        last = performance.now();
        requestAnimationFrame(step);
    }

    // A brief glow on the element particles arrive at. Restarting a CSS animation needs the class
    // off, a reflow, and the class back on.
    var lastFlash = 0;
    function flash(element) {
        var now = performance.now();
        if (now - lastFlash < 260) return;
        lastFlash = now;
        element.classList.remove('fx-hit');
        void element.offsetWidth;
        element.classList.add('fx-hit');
    }

    function centre(element) {
        var r = element.getBoundingClientRect();
        return { x: r.left + r.width / 2, y: r.top + r.height / 2, r: r };
    }

    function confetti(x, y, n, colours, power) {
        captureColliders();
        for (var i = 0; i < n; i++) {
            var a = -Math.PI / 2 + (Math.random() - 0.5) * 2.2;
            var s = (300 + Math.random() * 700) * power;
            spawn(CONFETTI, x, y, Math.cos(a) * s, Math.sin(a) * s, 3 + Math.random() * 2, 10 + Math.random() * 8, colours[i % colours.length]);
        }
    }

    function sparks(x, y, n, colours, speed) {
        for (var i = 0; i < n; i++) {
            var a = Math.random() * 6.283, s = speed * (0.4 + Math.random() * 0.6);
            spawn(SPARK, x, y, Math.cos(a) * s, Math.sin(a) * s, 0.8 + Math.random() * 0.9, 7 + Math.random() * 7, colours[i % colours.length]);
        }
    }

    function visible() { return visuals && !document.hidden && overlay(); }

    function row(id) { return document.querySelector('[data-repo="' + CSS.escape(id) + '"]'); }

    // ── SVG filters ────────────────────────────────────────────────────────────────────────
    //
    // Referenced from the stylesheets, which only use them under :root[data-fx] (animated ones)
    // or :root[data-liquid] (refraction), both set below once these exist: a url() pointing at a
    // filter that is not in the document hides the element in some engines.
    function edgeMap(axis) {
        // A gradient that is mid-grey (no displacement) across the middle and runs to 0 and 255 in
        // the last 12% at each edge: feDisplacementMap turns that into a lens bending the backdrop
        // at the panel's rim. One channel per image; the two are added in the filter.
        var stop = axis === 'x' ? ['#000000', '#800000', '#ff0000'] : ['#000000', '#008000', '#00ff00'];
        var end = axis === 'x' ? "x2='1' y2='0'" : "x2='0' y2='1'";
        return 'data:image/svg+xml,' + encodeURIComponent(
            "<svg xmlns='http://www.w3.org/2000/svg' width='100' height='100' preserveAspectRatio='none'>" +
            "<linearGradient id='g' x1='0' y1='0' " + end + ">" +
            "<stop offset='0' stop-color='" + stop[0] + "'/><stop offset='.12' stop-color='" + stop[1] + "'/>" +
            "<stop offset='.88' stop-color='" + stop[1] + "'/><stop offset='1' stop-color='" + stop[2] + "'/>" +
            "</linearGradient><rect width='100' height='100' fill='url(#g)'/></svg>");
    }

    function injectFilters() {
        var animate = function (attribute, values, seconds) {
            return visuals ? '<animate attributeName="' + attribute + '" values="' + values + '" dur="' + seconds + 's" repeatCount="indefinite"/>' : '';
        };
        var host = document.createElement('div');
        host.innerHTML =
            '<svg width="0" height="0" aria-hidden="true" focusable="false" style="position:absolute">' +
            // Liquid glass: blur, refract at the rim, push saturation back up. Used as a backdrop-filter.
            '<filter id="fx-liquid" x="0" y="0" width="100%" height="100%" color-interpolation-filters="sRGB">' +
            '<feImage href="' + edgeMap('x') + '" preserveAspectRatio="none" result="mx"/>' +
            '<feImage href="' + edgeMap('y') + '" preserveAspectRatio="none" result="my"/>' +
            '<feComposite in="mx" in2="my" operator="arithmetic" k2="1" k3="1" result="map"/>' +
            '<feGaussianBlur in="SourceGraphic" stdDeviation="10" result="soft"/>' +
            '<feDisplacementMap in="soft" in2="map" scale="46" xChannelSelector="R" yChannelSelector="G" result="bent"/>' +
            '<feColorMatrix in="bent" type="saturate" values="1.7"/>' +
            '</filter>' +
            // Heat haze on the hottest treemap tiles: slow fractal noise displacing the tile.
            '<filter id="fx-haze" x="-4%" y="-8%" width="108%" height="116%">' +
            '<feTurbulence type="fractalNoise" baseFrequency="0.012 0.06" numOctaves="2" seed="4" result="n">' +
            animate('baseFrequency', '0.012 0.06;0.016 0.09;0.012 0.06', 5) + '</feTurbulence>' +
            '<feDisplacementMap in="SourceGraphic" in2="n" scale="7" xChannelSelector="R" yChannelSelector="G"/>' +
            '</filter>' +
            // Streak flame: the same idea faster and stronger, then softened.
            '<filter id="fx-flame" x="-40%" y="-40%" width="180%" height="180%">' +
            '<feTurbulence type="fractalNoise" baseFrequency="0.09 0.16" numOctaves="2" seed="9" result="n">' +
            animate('baseFrequency', '0.09 0.16;0.11 0.24;0.09 0.16', 1.1) + '</feTurbulence>' +
            '<feDisplacementMap in="SourceGraphic" in2="n" scale="9" xChannelSelector="R" yChannelSelector="G" result="d"/>' +
            '<feGaussianBlur in="d" stdDeviation="0.5"/>' +
            '</filter>' +
            // Disintegration: noise both displaces the element and, through the alpha matrix, eats
            // it away as fx.disintegrate raises the threshold.
            '<filter id="fx-dissolve" x="-25%" y="-25%" width="150%" height="150%">' +
            '<feTurbulence type="fractalNoise" baseFrequency="0.03" numOctaves="3" seed="2" result="n"/>' +
            '<feDisplacementMap id="fx-dissolve-shift" in="SourceGraphic" in2="n" scale="0" xChannelSelector="R" yChannelSelector="G" result="d"/>' +
            '<feColorMatrix id="fx-dissolve-mask" in="n" type="matrix" values="0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 1" result="m"/>' +
            '<feComposite in="d" in2="m" operator="in"/>' +
            '</filter>' +
            '</svg>';
        document.body.appendChild(host.firstChild);
        if (visuals) document.documentElement.setAttribute('data-fx', 'on');
        // backdrop-filter: url() is a Chromium feature; elsewhere .u-glass keeps its plain blur.
        if (navigator.userAgentData) document.documentElement.setAttribute('data-liquid', 'on');
    }

    // ── Pointer surfaces ───────────────────────────────────────────────────────────────────
    //
    // .u-glass gets --mx/--my (a specular highlight follows the pointer, effects.css). .u-holo
    // tilts toward the pointer on a spring and moves its foil (--rx, --ry, --fx, --fy, --hyp); on
    // a phone, device tilt drives the same targets.
    var springs = new Map(), springing = false, lastSpring = 0;

    function aim(element, x, y) {
        var s = springs.get(element);
        if (!s) { s = { rx: 0, ry: 0, vrx: 0, vry: 0, trx: 0, try_: 0, fx: 0.5, fy: 0.5 }; springs.set(element, s); }
        s.trx = (0.5 - y) * 16;
        s.try_ = (x - 0.5) * 18;
        s.fx = x;
        s.fy = y;
        if (!springing) { springing = true; lastSpring = performance.now(); requestAnimationFrame(settle); }
    }

    // Slightly underdamped (k 140, c 13): one small overshoot as a card swings back, like a real
    // one on a stand.
    function settle(now) {
        var dt = Math.min(0.033, (now - lastSpring) / 1000), moving = false;
        lastSpring = now;
        springs.forEach(function (s, element) {
            if (!element.isConnected) { springs.delete(element); return; }
            s.vrx += (140 * (s.trx - s.rx) - 13 * s.vrx) * dt;
            s.vry += (140 * (s.try_ - s.ry) - 13 * s.vry) * dt;
            s.rx += s.vrx * dt;
            s.ry += s.vry * dt;
            var hyp = Math.min(1, Math.hypot(s.fx - 0.5, s.fy - 0.5) * 2);
            element.style.setProperty('--rx', s.rx.toFixed(2) + 'deg');
            element.style.setProperty('--ry', s.ry.toFixed(2) + 'deg');
            element.style.setProperty('--fx', (s.fx * 100).toFixed(1) + '%');
            element.style.setProperty('--fy', (s.fy * 100).toFixed(1) + '%');
            element.style.setProperty('--hyp', hyp.toFixed(3));
            if (Math.abs(s.vrx) + Math.abs(s.vry) + Math.abs(s.trx - s.rx) + Math.abs(s.try_ - s.ry) > 0.02) moving = true;
        });
        if (moving) requestAnimationFrame(settle);
        else springing = false;
    }

    function pointerSurfaces() {
        document.addEventListener('pointermove', function (e) {
            if (!e.target.closest) return;
            var glass = e.target.closest('.u-glass');
            if (glass) {
                var g = glass.getBoundingClientRect();
                glass.style.setProperty('--mx', (e.clientX - g.left).toFixed(0) + 'px');
                glass.style.setProperty('--my', (e.clientY - g.top).toFixed(0) + 'px');
            }
            var holo = visuals && e.target.closest('.u-holo');
            if (holo) {
                var h = holo.getBoundingClientRect();
                aim(holo, (e.clientX - h.left) / h.width, (e.clientY - h.top) / h.height);
            }
        }, { passive: true });

        document.addEventListener('pointerout', function (e) {
            var holo = e.target.closest && e.target.closest('.u-holo');
            if (holo && !holo.contains(e.relatedTarget)) aim(holo, 0.5, 0.5);
        }, { passive: true });

        // Android fires this without a permission prompt; iOS needs one, and a prompt is not worth
        // a foil sheen, so there it is pointer-only.
        if (visuals) {
            window.addEventListener('deviceorientation', function (e) {
                if (e.gamma == null) return;
                var x = 0.5 + Math.max(-0.5, Math.min(0.5, e.gamma / 60));
                var y = 0.5 + Math.max(-0.5, Math.min(0.5, (e.beta - 45) / 60));
                document.querySelectorAll('.u-holo').forEach(function (holo) { aim(holo, x, y); });
            }, { passive: true });
        }
    }

    // ── Hotspot embers ─────────────────────────────────────────────────────────────────────
    //
    // A treemap tile carrying data-heat (1-4) sheds embers from its top edge while hovered, at a
    // rate set by its heat, and rumbles once on entry, pitched by heat.
    var emberTile = null;
    function embers() {
        document.addEventListener('pointerover', function (e) {
            var tile = e.target.closest && e.target.closest('[data-heat]');
            if (!tile || tile === emberTile) return;
            emberTile = tile;
            var heat = +tile.getAttribute('data-heat') || 1;
            sound(function (t) {
                note(38 + heat * 9, t, 0.6, 0.12, 'sine', false, 30 + heat * 6);
                noise(t, 0.5, 0.025 * heat, 'lowpass', 400 + heat * 150, 120);
            });
            if (!visible()) return;
            var owed = 0;
            emitters.push(function (dt) {
                if (emberTile !== tile || !tile.isConnected) return false;
                var r = tile.getBoundingClientRect();
                for (owed += dt * heat * 28; owed >= 1; owed--) {
                    var hot = Math.random();
                    spawn(EMBER, r.left + Math.random() * r.width, r.top + Math.random() * Math.min(12, r.height),
                        (Math.random() - 0.5) * 40, -40 - Math.random() * 80, 1 + Math.random() * 1.2, 4 + Math.random() * 6,
                        [1, 0.45 + hot * 0.45, 0.1 + hot * 0.2]);
                }
            });
            run();
        }, { passive: true });

        document.addEventListener('pointerout', function (e) {
            if (emberTile && e.target.closest && e.target.closest('[data-heat]') === emberTile && !emberTile.contains(e.relatedTarget)) emberTile = null;
        }, { passive: true });
    }

    // ── Public entry points ────────────────────────────────────────────────────────────────
    window.fx = {
        // An analysis progress frame: commits and lines since the previous frame. Particles in the
        // repository's file-type colours stream from its grid row into the Total Lines tile, and
        // a tick sounds.
        progress: function (id, commits, lines, extensions) {
            try {
                if (commits <= 0 && lines <= 0) return;
                tick(lines, commits);
                var from = row(id);
                if (!from || !visible()) return;
                var a = from.getBoundingClientRect();
                riverTarget = document.querySelector('[data-fx="lines"]');
                var b = riverTarget ? centre(riverTarget) : { x: innerWidth / 2, y: 0 };
                var colours = palette(extensions);
                var n = Math.min(260, 10 + commits * 6 + Math.log2(1 + lines) * 2);
                for (var i = 0; i < n; i++) {
                    var p = spawn(FLOW, a.left + Math.random() * Math.min(a.width, 320), a.top + Math.random() * a.height,
                        (Math.random() - 0.5) * 240, -80 - Math.random() * 220, 4, 5 + Math.random() * 6, colours[i % colours.length]);
                    if (p < 0) break;
                    tx[p] = b.x + (Math.random() - 0.5) * 40;
                    ty[p] = b.y + (Math.random() - 0.5) * 20;
                }
                run();
            } catch (e) { }
        },

        // A finished analysis. A shockwave through the backdrop from the row, then confetti in the
        // repository's colours, more of it the bigger the repository, landing on the cards.
        supernova: function (id, totalLines, extensions) {
            try {
                var from = row(id);
                if (!from || !visible()) return;
                var r = from.getBoundingClientRect();
                var x = r.left + Math.min(r.width * 0.3, 240), y = r.top + r.height / 2;
                if (window.backdrop) backdrop.pulse(x, y);
                var colours = palette(extensions);
                sparks(x, y, 70, colours, 700);
                confetti(x, y, Math.round(60 + 70 * Math.log10(1 + Math.max(0, totalLines))), colours, 1);
                run();
            } catch (e) { }
        },

        // An odometer changed `changed` digits; `milestone` when it crossed a power of ten.
        odometer: function (element, changed, milestone) {
            try {
                sound(function (t) {
                    for (var i = 0; i < Math.min(changed, 7); i++) noise(t + i * 0.045, 0.03, 0.08, 'bandpass', 2600, 1800, 8);
                });
                if (!milestone) return;
                sound(function (t) {
                    [0, 2, 4, 7].forEach(function (d, i) { note(degree(10 + d), t + 0.25 + i * 0.08, 0.7, 0.07, 'triangle', true); });
                });
                if (navigator.vibrate) navigator.vibrate([30, 40, 30]);
                if (!element || !visible()) return;
                var c = centre(element);
                sparks(c.x, c.y, 50, BRAND, 500);
                confetti(c.x, c.y, 120, BRAND, 0.8);
                if (window.backdrop) backdrop.pulse(c.x, c.y);
                run();
            } catch (e) { }
        },

        // Five shells over the element, each a ring of sparks with a pop and a crackle.
        fireworks: function (element) {
            try {
                if (!element) return;
                for (var shell = 0; shell < 5; shell++) {
                    setTimeout(function () {
                        if (!element.isConnected) return;
                        var r = element.getBoundingClientRect();
                        var x = r.left + r.width * (0.15 + Math.random() * 0.7), y = r.top + r.height * (0.15 + Math.random() * 0.35);
                        var hue = Math.random() * 360;
                        sound(function (t) {
                            noise(t, 0.25, 0.18, 'lowpass', 1800, 200);
                            for (var i = 0; i < 6; i++) noise(t + 0.12 + Math.random() * 0.5, 0.03, 0.04, 'highpass', 4000, 6000);
                        });
                        if (!visible()) return;
                        sparks(x, y, 110, [hsl(hue, 1, 0.65), hsl((hue + 40) % 360, 1, 0.7), [1, 1, 0.9]], 520);
                        run();
                    }, shell * 380);
                }
            } catch (e) { }
        },

        // The year as a phrase: twelve rising notes, one per month, each as loud as that month was
        // busy (commits per month, comma-separated), resolving on a held chord.
        yearChord: function (monthly) {
            var months = split(monthly).map(Number);
            var max = Math.max.apply(null, months.concat([1]));
            sound(function (t) {
                months.forEach(function (commits, m) {
                    note(degree(5 + m), t + m * 0.11, 0.9, 0.02 + 0.08 * commits / max, 'triangle', true);
                });
                var end = t + months.length * 0.11;
                [5, 7, 10, 15].forEach(function (d) { note(degree(d), end, 2.4, 0.05, 'sine', true); });
            });
        },

        // Dissolves an element into drifting dust: an SVG noise mask eats it while particles leave
        // from its surface, over a low whoosh and a sub thump. Resolves when done, or at once
        // without motion; the caller navigates away afterwards, so the element is never restored.
        disintegrate: function (element) {
            return new Promise(function (resolve) {
                try {
                    sound(function (t) {
                        noise(t, 1.6, 0.16, 'lowpass', 3200, 160);
                        note(70, t, 0.6, 0.22, 'sine', false, 28);
                    });
                    if (!element || !visible()) { resolve(); return; }
                    var shift = document.getElementById('fx-dissolve-shift');
                    var mask = document.getElementById('fx-dissolve-mask');
                    var r = element.getBoundingClientRect(), start = performance.now(), duration = 1600, owed = 0;
                    element.style.filter = 'url(#fx-dissolve)';
                    emitters.push(function (dt) {
                        var t = Math.min(1, (performance.now() - start) / duration);
                        // alpha = 8 * (noise - threshold). Fractal noise sits mostly between 0.3
                        // and 0.75, so the threshold sweeps across that band: holes open slowly,
                        // then the last islands go together.
                        var threshold = 0.28 + t * 0.5;
                        mask.setAttribute('values', '0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 8 0 0 0 ' + (-threshold * 8).toFixed(3));
                        shift.setAttribute('scale', (t * t * 90).toFixed(1));
                        for (owed += dt * 900 * (1 - t * 0.6); owed >= 1; owed--) {
                            var grey = 0.4 + Math.random() * 0.4;
                            spawn(DUST, r.left + Math.random() * r.width, r.top + Math.random() * r.height,
                                (Math.random() - 0.2) * 60, -Math.random() * 50, 1.2 + Math.random() * 1.4, 3 + Math.random() * 4,
                                [grey, grey * 0.95, grey * 1.05]);
                        }
                        if (t < 1) return true;
                        element.style.opacity = '0';
                        resolve();
                        return false;
                    });
                    run();
                    setTimeout(resolve, duration + 600); // never strand the caller on a missed frame
                } catch (e) { resolve(); }
            });
        }
    };

    try {
        injectFilters();
        pointerSurfaces();
        embers();
    } catch (e) { }
})();
