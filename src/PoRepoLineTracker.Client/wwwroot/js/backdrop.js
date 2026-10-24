// Animated background: one fixed WebGL2 canvas behind the app shell.
//
// ON by default (the owner's choice; there is no settings switch). localStorage 'backdrop' = 'off'
// — set by hand in devtools — is the only way to disable it. Under prefers-reduced-motion it is
// one still frame, redrawn only when the theme, the size or the mood changes, and no shockwave.
// Without WebGL2 it does not start — the app's ordinary CSS background stays exactly as it is,
// because the rules that let the canvas show through (app.css, `data-backdrop`) only apply
// once a first frame has actually been drawn. Nothing here is required by anything else: every
// entry point is wrapped, and a failure leaves the page as if this file had not loaded.
//
// Two inputs from the app, both through window.backdrop (no-ops when the backdrop is not running):
//   - mood(energy, heat), 0-1 each, from the portfolio's last 30 days (Repositories page): energy
//     is how many days had commits and speeds the aurora up and brightens it; heat is how much
//     code was added relative to the portfolio's size and warms it toward the accent colour.
//     Remembered in localStorage 'backdrop-mood' so every page, and the next visit, opens on it.
//   - pulse(x, y), viewport pixels: a shockwave ring that refracts the aurora as it spreads
//     (fx.js, when an analysis finishes or a total crosses a milestone).
(function () {
    var canvas = null, gl = null, uniforms = null, observer = null, timer = 0, frameRequest = 0, last = 0;
    var mood = [0.3, 0.2], target = [0.3, 0.2], pulseStart = -1, still = false;
    var SCALE = 0.5, PULSE_MS = 1700;

    // One triangle covering the viewport, generated from gl_VertexID — no buffers to manage.
    var VERTEX = '#version 300 es\n' +
        'void main(){gl_Position=vec4(gl_VertexID==1?3.:-1.,gl_VertexID==2?3.:-1.,0.,1.);}';

    // The page's surface colour with an aurora of the brand colours over it. The aurora is fbm
    // noise domain-warped twice (Inigo Quilez's "warp" construction: noise of noise of noise),
    // which is what turns soft blobs into folded, curtain-like bands; a sine across x stretches
    // them into vertical drapes. Strength stays low (7-15% at a curtain's core) so the well still
    // reads as the same surface; cards are opaque and sit above it.
    //
    // The pulse displaces the sample point outward along a gaussian ring and samples the aurora
    // three times with slightly different displacement for red, green and blue: chromatic
    // aberration, the rainbow fringe real refraction has. The extra samples are taken only inside
    // the ring. The last line is half a step of noise, which stops a gradient this shallow banding.
    var FRAGMENT = '#version 300 es\n' +
        'precision highp float;' +
        'uniform vec2 size,mood;uniform vec4 pulse;uniform float time;uniform vec3 base,tintA,tintB;out vec4 colour;' +
        'float hash(vec2 p){return fract(sin(dot(p,vec2(127.1,311.7)))*43758.5453);}' +
        'float noise(vec2 p){vec2 i=floor(p),f=fract(p),u=f*f*(3.-2.*f);' +
        'return mix(mix(hash(i),hash(i+vec2(1,0)),u.x),mix(hash(i+vec2(0,1)),hash(i+vec2(1,1)),u.x),u.y);}' +
        'float fbm(vec2 p){float v=0.,a=.5;mat2 r=mat2(.8,.6,-.6,.8);for(int i=0;i<5;i++){v+=a*noise(p);p=r*p*2.03;a*=.5;}return v;}' +
        'vec3 aurora(vec2 p,float t){' +
        'vec2 q=vec2(fbm(p+vec2(0.,t*.05)),fbm(p+vec2(5.2,1.3)-t*.04));' +
        'vec2 r=vec2(fbm(p+3.*q+vec2(1.7,9.2)+t*.06),fbm(p+3.*q+vec2(8.3,2.8)));' +
        'float f=fbm(p+3.*r);' +
        'float drape=smoothstep(.3,.85,f)*(.55+.45*sin(p.x*3.1+r.y*4.+t*.2));' +
        'vec3 tint=mix(tintA,tintB,clamp(r.x*1.2-.35+mood.y*.7,0.,1.));' +
        'return mix(base,tint,(.07+.08*mood.x)*drape+.025*f);}' +
        'void main(){' +
        'vec2 frag=gl_FragCoord.xy;float t=time*(.5+1.5*mood.x);float unit=size.y*.55;' +
        'vec2 p=frag/unit;vec3 c;' +
        'float ring=0.;vec2 dir=vec2(0.);' +
        'if(pulse.z>=0.){vec2 d=frag-pulse.xy;float dist=length(d);float radius=pulse.z*length(size)*.75;float w=length(size)*.05;' +
        'ring=exp(-pow((dist-radius)/w,2.))*(1.-pulse.z/1.7);dir=d/max(dist,1.);}' +
        'if(ring>.003){vec2 s=dir*ring*.22;' +
        'c=vec3(aurora(p-s,t).r,aurora(p-s*1.25,t).g,aurora(p-s*1.5,t).b);' +
        'c+=ring*.18*mix(tintA,tintB,.5)+ring*.06;}' +
        'else c=aurora(p,t);' +
        'c+=(fract(sin(dot(frag,vec2(12.9898,78.233)))*43758.5453)-.5)/255.;' +
        'colour=vec4(c,1.);}';

    // Resolved through the canvas's own `color`, so any CSS colour syntax a token uses comes
    // back as rgb(r, g, b).
    function token(name) {
        canvas.style.color = 'var(' + name + ')';
        var parts = getComputedStyle(canvas).color.match(/[\d.]+/g) || [0, 0, 0];
        return [parts[0] / 255, parts[1] / 255, parts[2] / 255];
    }

    function readColours() {
        gl.uniform3fv(uniforms.base, token('--bg-secondary'));
        gl.uniform3fv(uniforms.tintA, token('--primary-color'));
        gl.uniform3fv(uniforms.tintB, token('--accent-secondary'));
    }

    function draw(now) {
        last = now;
        // Eased toward the target rather than jumped, so a new mood drifts in over a few seconds.
        // A still frame has no next frame to drift over, so it takes the target at once.
        var ease = still ? 1 : 0.04;
        mood[0] += (target[0] - mood[0]) * ease;
        mood[1] += (target[1] - mood[1]) * ease;
        gl.uniform2f(uniforms.mood, mood[0], mood[1]);
        gl.uniform1f(uniforms.time, now / 1000);
        var age = pulseStart < 0 ? -1 : (now - pulseStart) / 1000;
        gl.uniform4f(uniforms.pulse, pulse.x, pulse.y, age, 0);
        gl.drawArrays(gl.TRIANGLES, 0, 3);
    }

    // A backdrop this soft gains nothing from native resolution: drawn at half the CSS size and
    // scaled up by the browser, a quarter of the pixels of the old 1x-1.5x canvas.
    function resize() {
        canvas.width = Math.max(1, Math.round(window.innerWidth * SCALE));
        canvas.height = Math.max(1, Math.round(window.innerHeight * SCALE));
        gl.viewport(0, 0, canvas.width, canvas.height);
        gl.uniform2f(uniforms.size, canvas.width, canvas.height);
        draw(last); // sizing clears the canvas; repaint now rather than show a blank frame
    }

    // ~10fps on a timer, not a requestAnimationFrame loop: at rest the aurora moves well under one
    // colour level per frame. Only while a pulse is spreading does it run at the display's rate,
    // for under two seconds, then drop back.
    function frame() {
        var now = performance.now();
        draw(now);
        if (pulseStart >= 0 && now - pulseStart < PULSE_MS) {
            frameRequest = requestAnimationFrame(frame);
        } else {
            pulseStart = -1;
            timer = setTimeout(frame, 100);
        }
    }

    function halt() {
        clearTimeout(timer);
        cancelAnimationFrame(frameRequest);
    }

    function onVisibility() {
        halt();
        if (document.hidden) return;
        if (still) draw(performance.now());
        else frame();
    }

    function compile(type, source) {
        var shader = gl.createShader(type);
        gl.shaderSource(shader, source);
        gl.compileShader(shader);
        return shader;
    }

    function stop() {
        halt();
        window.removeEventListener('resize', resize);
        document.removeEventListener('visibilitychange', onVisibility);
        if (observer) observer.disconnect();
        document.documentElement.removeAttribute('data-backdrop');
        if (gl) {
            // Browsers cap live WebGL contexts per page; dropping the canvas alone leaves this
            // one counted until garbage collection gets round to it.
            var lose = gl.getExtension('WEBGL_lose_context');
            if (lose) lose.loseContext();
        }
        if (canvas) canvas.remove();
        canvas = gl = uniforms = observer = null;
    }

    var pulse = { x: 0, y: 0 };

    window.backdrop = {
        mood: function (energy, heat) {
            target = [Math.max(0, Math.min(1, +energy || 0)), Math.max(0, Math.min(1, +heat || 0))];
            try { localStorage.setItem('backdrop-mood', JSON.stringify(target)); } catch (e) { }
            if (gl && still) draw(performance.now());
        },
        pulse: function (x, y) {
            if (!gl || still || document.hidden) return;
            pulse.x = x * SCALE;
            pulse.y = (window.innerHeight - y) * SCALE; // GL's y runs up
            pulseStart = performance.now();
            halt();
            frame();
        }
    };

    function start() {
        try {
            // index.html's global, defined by the inline boot script before this deferred file runs.
            still = prefersReducedMotion();
            try {
                var saved = JSON.parse(localStorage.getItem('backdrop-mood'));
                if (saved && saved.length === 2) mood = target = [+saved[0] || 0, +saved[1] || 0];
                mood = mood.slice();
            } catch (e) { }

            canvas = document.createElement('canvas');
            canvas.id = 'backdrop';
            canvas.setAttribute('aria-hidden', 'true');
            gl = canvas.getContext('webgl2', { alpha: false, antialias: false, powerPreference: 'low-power' });
            if (!gl) { canvas = null; return; }

            var program = gl.createProgram();
            gl.attachShader(program, compile(gl.VERTEX_SHADER, VERTEX));
            gl.attachShader(program, compile(gl.FRAGMENT_SHADER, FRAGMENT));
            gl.linkProgram(program);
            if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(program));
            gl.useProgram(program);

            uniforms = {};
            ['size', 'time', 'base', 'tintA', 'tintB', 'mood', 'pulse'].forEach(function (name) {
                uniforms[name] = gl.getUniformLocation(program, name);
            });

            // In the document before the colours are read: var() only resolves on an attached element.
            document.body.prepend(canvas);
            readColours();
            resize();

            // A lost context (GPU reset, driver update) is not worth restoring for decoration.
            canvas.addEventListener('webglcontextlost', stop);
            window.addEventListener('resize', resize);
            document.addEventListener('visibilitychange', onVisibility);
            observer = new MutationObserver(function () { readColours(); draw(last); });
            observer.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });

            // Only now: this is what makes the shell's own backgrounds transparent.
            document.documentElement.setAttribute('data-backdrop', 'on');
            onVisibility();
        } catch (e) {
            stop();
        }
    }

    var off = false;
    try { off = localStorage.getItem('backdrop') === 'off'; } catch (e) { }
    if (!off) start();
})();
