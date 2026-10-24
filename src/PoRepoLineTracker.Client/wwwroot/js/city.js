// Year in Review hero: the year's commits as a city. One tower per day on a 53-week x 7-day
// grid (the same layout as a contribution calendar), height by that day's commits. Imported by
// YearInReview.razor only, so no other page pays for it.
//
// WebGL2, no library: instanced cubes (one draw call for 365 towers), a procedural ground grid,
// and additive glow sprites over the busiest days. Towers rise on a damped spring, staggered
// through the year. Shading is a directional light, fake ambient occlusion darkening each
// tower's foot, a Blinn-Phong highlight, a fresnel rim so the towers read as glass, and lit
// windows, more of them the taller the tower. The camera orbits slowly and flies to the day or
// month a hovered tile is about.
//
// Under reduced motion it is a still picture: fully built, the camera cut (not flown) to each
// framing, one frame drawn per change and no loop. mount() always returns a controller; without
// WebGL2 it draws nothing, every method is a no-op, and the banner keeps its CSS gradient.

const TOWER_VERTEX = `#version 300 es
layout(location=0) in vec3 pos;
layout(location=1) in vec3 nrm;
layout(location=2) in vec4 inst; // x, z, height, day of year (0-based)
uniform mat4 viewProj;
uniform float time, focusDay;
out vec3 vN, vW;
out float vFoot, vTop, vGlow;
void main(){
    float t = max(time - inst.w / 365. * 1.4, 0.);
    float rise = t <= 0. ? 0. : 1. - exp(-5. * t) * cos(10. * t);   // damped spring: overshoot, settle
    float h = inst.z * rise;
    vW = vec3(inst.x + pos.x * .78, pos.y * h, inst.y + pos.z * .78);
    vN = nrm;
    vFoot = pos.y;
    vTop = inst.z;
    vGlow = exp(-pow((inst.w - focusDay) / 2.5, 2.));
    gl_Position = viewProj * vec4(vW, 1.);
}`;

const TOWER_FRAGMENT = `#version 300 es
precision highp float;
in vec3 vN, vW;
in float vFoot, vTop, vGlow;
uniform vec3 camPos, low, high, glow;
uniform float maxH;
out vec4 o;
float hash(vec3 p){ return fract(sin(dot(p, vec3(12.9898, 78.233, 37.719))) * 43758.5453); }
void main(){
    vec3 n = normalize(vN);
    float k = clamp(vTop / maxH, 0., 1.);
    vec3 base = mix(low, high, k);
    vec3 L = normalize(vec3(-.4, .9, .35));
    vec3 V = normalize(camPos - vW);
    float diffuse = max(dot(n, L), 0.) * .55 + .45;
    float ao = mix(.35, 1., smoothstep(0., .6, vFoot));
    float spec = pow(max(dot(n, normalize(L + V)), 0.), 48.) * .5;
    float rim = pow(1. - max(dot(n, V), 0.), 3.) * .7;
    vec3 c = base * diffuse * ao + spec + rim * high;
    if (abs(n.y) < .5) {
        vec3 cell = floor(vec3(vW.x * 5., vW.y * 3., vW.z * 5.));
        vec2 f = fract(vec2((vW.x + vW.z) * 5., vW.y * 3.));
        float lit = step(.55 - k * .35, hash(cell)) * step(.25, f.x) * step(f.x, .75) * step(.3, f.y) * step(f.y, .7);
        c += lit * vec3(1., .85, .55) * (.35 + .5 * k);
    }
    c += vGlow * glow * (.6 + .8 * vFoot);
    float fog = exp(-length(camPos - vW) * .016);
    float a = .94 * fog;
    o = vec4(c * a, a);
}`;

const GROUND_VERTEX = `#version 300 es
uniform mat4 viewProj;
out vec2 vXZ;
void main(){
    vec2 corner = vec2(gl_VertexID == 1 || gl_VertexID == 2 || gl_VertexID == 4 ? 1. : -1., gl_VertexID >= 2 && gl_VertexID <= 4 ? 1. : -1.);
    vXZ = corner * 90.;
    gl_Position = viewProj * vec4(vXZ.x, 0., vXZ.y, 1.);
}`;

const GROUND_FRAGMENT = `#version 300 es
precision highp float;
in vec2 vXZ;
uniform vec3 high;
out vec4 o;
void main(){
    vec2 g = abs(fract(vXZ + .5) - .5) / fwidth(vXZ);
    float line = 1. - min(min(g.x, g.y), 1.);
    float fade = exp(-length(vXZ) * .035);
    float a = (.05 + line * .28) * fade;
    o = vec4(high * a, a);
}`;

const GLOW_VERTEX = `#version 300 es
layout(location=0) in vec4 peak; // x, z, height, strength
uniform mat4 viewProj;
uniform float time, scale;
out float vStrength;
void main(){
    float settle = clamp(time - .8, 0., 1.);
    vec4 clip = viewProj * vec4(peak.x, peak.z + .4, peak.y, 1.);
    gl_Position = clip;
    gl_PointSize = scale * (2. + peak.w * 5.) / clip.w * settle;
    vStrength = peak.w * (.85 + .15 * sin(time * 3. + peak.x));
}`;

const GLOW_FRAGMENT = `#version 300 es
precision mediump float;
in float vStrength;
uniform vec3 glow;
out vec4 o;
void main(){
    float d = length(gl_PointCoord * 2. - 1.);
    float a = exp(-d * d * 6.) * vStrength * .55;   // a halo, not a lamp: slide text sits over it
    o = vec4(glow * a, 0.);   // premultiplied with alpha 0: additive
}`;

// 36 vertices of a cube with its foot on y = 0, interleaved position + normal.
function cube() {
    const faces = [
        [[1, 0, 0], [[.5, 0, -.5], [.5, 1, -.5], [.5, 1, .5], [.5, 0, .5]]],
        [[-1, 0, 0], [[-.5, 0, .5], [-.5, 1, .5], [-.5, 1, -.5], [-.5, 0, -.5]]],
        [[0, 1, 0], [[-.5, 1, -.5], [-.5, 1, .5], [.5, 1, .5], [.5, 1, -.5]]],
        [[0, 0, 1], [[.5, 0, .5], [.5, 1, .5], [-.5, 1, .5], [-.5, 0, .5]]],
        [[0, 0, -1], [[-.5, 0, -.5], [-.5, 1, -.5], [.5, 1, -.5], [.5, 0, -.5]]]
    ];
    const out = [];
    for (const [n, q] of faces) for (const i of [0, 1, 2, 0, 2, 3]) out.push(...q[i], ...n);
    return new Float32Array(out);
}

function perspective(fov, aspect, near, far) {
    const f = 1 / Math.tan(fov / 2), r = 1 / (near - far);
    return [f / aspect, 0, 0, 0, 0, f, 0, 0, 0, 0, (far + near) * r, -1, 0, 0, 2 * far * near * r, 0];
}

function lookAt(eye, at) {
    const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
    const norm = v => { const l = Math.hypot(...v) || 1; return [v[0] / l, v[1] / l, v[2] / l]; };
    const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
    const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    const z = norm(sub(eye, at)), x = norm(cross([0, 1, 0], z)), y = cross(z, x);
    return [x[0], y[0], z[0], 0, x[1], y[1], z[1], 0, x[2], y[2], z[2], 0, -dot(x, eye), -dot(y, eye), -dot(z, eye), 1];
}

function multiply(a, b) {
    const out = new Array(16);
    for (let c = 0; c < 4; c++) for (let r = 0; r < 4; r++) {
        let s = 0;
        for (let k = 0; k < 4; k++) s += a[k * 4 + r] * b[c * 4 + k];
        out[c * 4 + r] = s;
    }
    return out;
}

function program(gl, vertex, fragment) {
    const p = gl.createProgram();
    for (const [type, source] of [[gl.VERTEX_SHADER, vertex], [gl.FRAGMENT_SHADER, fragment]]) {
        const s = gl.createShader(type);
        gl.shaderSource(s, source);
        gl.compileShader(s);
        if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s));
        gl.attachShader(p, s);
    }
    gl.linkProgram(p);
    if (!gl.getProgramParameter(p, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(p));
    const u = {};
    const n = gl.getProgramParameter(p, gl.ACTIVE_UNIFORMS);
    for (let i = 0; i < n; i++) { const name = gl.getActiveUniform(p, i).name; u[name] = gl.getUniformLocation(p, name); }
    return { p, u };
}

const LOW = [0.36, 0.25, 0.85], HIGH = [1.0, 0.45, 0.78], GLOW = [1.0, 0.8, 0.4];
const NONE = { focus: () => { }, dispose: () => { } };

// daily: commits per day of the year, comma-separated (365 or 366 values). year: for the weekday
// the year starts on, which sets each day's row.
export function mount(canvas, daily, year) {
    if (!canvas) return NONE;
    const still = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    const gl = canvas.getContext('webgl2', { premultipliedAlpha: true, antialias: true });
    if (!gl) return NONE;

    try {
        const days = (daily || '').split(',').map(Number);
        const max = Math.max(1, ...days);
        const firstWeekday = new Date(year, 0, 1).getDay();
        const position = d => [Math.floor((d + firstWeekday) / 7) - 26, (d + firstWeekday) % 7 - 3];

        const instances = new Float32Array(days.length * 4);
        days.forEach((commits, d) => {
            const [x, z] = position(d);
            instances.set([x, z, commits > 0 ? 0.35 + 9 * Math.sqrt(commits / max) : 0.06, d], d * 4);
        });

        // The five busiest days get a glow sprite above them, brightest first.
        const peaks = days.map((c, d) => [c, d]).filter(p => p[0] > 0).sort((a, b) => b[0] - a[0]).slice(0, 5);
        const glowData = new Float32Array(peaks.length * 4);
        peaks.forEach(([c, d], i) => {
            const [x, z] = position(d);
            glowData.set([x, z, 0.35 + 9 * Math.sqrt(c / max), 1 - i * 0.15], i * 4);
        });

        const tower = program(gl, TOWER_VERTEX, TOWER_FRAGMENT);
        const ground = program(gl, GROUND_VERTEX, GROUND_FRAGMENT);
        const glow = program(gl, GLOW_VERTEX, GLOW_FRAGMENT);

        const towerVao = gl.createVertexArray();
        gl.bindVertexArray(towerVao);
        gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
        gl.bufferData(gl.ARRAY_BUFFER, cube(), gl.STATIC_DRAW);
        gl.enableVertexAttribArray(0);
        gl.vertexAttribPointer(0, 3, gl.FLOAT, false, 24, 0);
        gl.enableVertexAttribArray(1);
        gl.vertexAttribPointer(1, 3, gl.FLOAT, false, 24, 12);
        gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
        gl.bufferData(gl.ARRAY_BUFFER, instances, gl.STATIC_DRAW);
        gl.enableVertexAttribArray(2);
        gl.vertexAttribPointer(2, 4, gl.FLOAT, false, 0, 0);
        gl.vertexAttribDivisor(2, 1);

        const glowVao = gl.createVertexArray();
        gl.bindVertexArray(glowVao);
        gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
        gl.bufferData(gl.ARRAY_BUFFER, glowData, gl.STATIC_DRAW);
        gl.enableVertexAttribArray(0);
        gl.vertexAttribPointer(0, 4, gl.FLOAT, false, 0, 0);
        gl.bindVertexArray(null);

        gl.enable(gl.BLEND);
        gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA);

        // Camera framings: azimuth, elevation, distance. A tile about one day or one month swaps
        // the look-at point for that place and moves in; any other tile picks a framing.
        const FRAMES = [[-0.5, 0.42, 66], [0.35, 0.3, 54], [-0.9, 0.55, 48], [0.7, 0.36, 50], [-0.2, 0.62, 58], [1.0, 0.3, 46]];
        const camera = { az: -1.4, el: 0.9, dist: 110, x: 0, z: 0 };
        let goal = { az: FRAMES[0][0], el: FRAMES[0][1], dist: FRAMES[0][2], x: 0, z: 0 };
        let focusDay = -100, start = performance.now(), last = start, frame = 0, disposed = false;

        const resize = () => {
            const dpr = Math.min(window.devicePixelRatio || 1, 2);
            canvas.width = Math.max(1, Math.round(canvas.clientWidth * dpr));
            canvas.height = Math.max(1, Math.round(canvas.clientHeight * dpr));
            gl.viewport(0, 0, canvas.width, canvas.height);
        };
        const observer = new ResizeObserver(() => { resize(); redraw(); });
        observer.observe(canvas);
        resize();

        let controller = null;
        const draw = now => {
            frame = 0;
            if (disposed) return;
            // Blazor removes the canvas when the page changes year or navigates away; the loop
            // and the GL context go with it rather than wait for an interop call.
            if (!canvas.isConnected) { controller.dispose(); return; }
            if (!still) frame = requestAnimationFrame(draw);
            if (document.hidden) return;
            const dt = Math.min(0.05, (now - last) / 1000);
            last = now;
            const t = still ? 100 : (now - start) / 1000; // still: every tower already risen

            // Exponential ease toward the goal (frame-rate independent), plus a slow idle orbit.
            // Still, the camera cuts straight to the goal.
            const ease = still ? 1 : 1 - Math.exp(-dt * 1.8);
            if (!still) goal.az += dt * 0.025;
            for (const key of ['az', 'el', 'dist', 'x', 'z']) camera[key] += (goal[key] - camera[key]) * ease;

            const target = [camera.x, 3.5, camera.z];
            const eye = [
                target[0] + Math.sin(camera.az) * Math.cos(camera.el) * camera.dist,
                target[1] + Math.sin(camera.el) * camera.dist,
                target[2] + Math.cos(camera.az) * Math.cos(camera.el) * camera.dist
            ];
            // On a wide banner the headline sits on the left, so the city is shifted right in clip
            // space (x' = x + shift * w) rather than aimed off-centre, which would turn it.
            const shift = canvas.clientWidth > 640 ? 0.3 : 0;
            const clipShift = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, shift, 0, 0, 1];
            const viewProj = multiply(clipShift, multiply(perspective(0.75, canvas.width / canvas.height, 0.5, 400), lookAt(eye, target)));

            gl.clearColor(0, 0, 0, 0);
            gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);

            gl.disable(gl.DEPTH_TEST);
            gl.useProgram(ground.p);
            gl.uniformMatrix4fv(ground.u.viewProj, false, viewProj);
            gl.uniform3fv(ground.u.high, HIGH);
            gl.drawArrays(gl.TRIANGLES, 0, 6);

            gl.enable(gl.DEPTH_TEST);
            gl.useProgram(tower.p);
            gl.uniformMatrix4fv(tower.u.viewProj, false, viewProj);
            gl.uniform1f(tower.u.time, t);
            gl.uniform1f(tower.u.focusDay, focusDay);
            gl.uniform1f(tower.u.maxH, 9.35);
            gl.uniform3fv(tower.u.camPos, eye);
            gl.uniform3fv(tower.u.low, LOW);
            gl.uniform3fv(tower.u.high, HIGH);
            gl.uniform3fv(tower.u.glow, GLOW);
            gl.bindVertexArray(towerVao);
            gl.drawArraysInstanced(gl.TRIANGLES, 0, 30, days.length);

            if (peaks.length) {
                gl.disable(gl.DEPTH_TEST);
                gl.useProgram(glow.p);
                gl.uniformMatrix4fv(glow.u.viewProj, false, viewProj);
                gl.uniform1f(glow.u.time, t);
                gl.uniform1f(glow.u.scale, canvas.height * 0.45);
                gl.uniform3fv(glow.u.glow, GLOW);
                gl.bindVertexArray(glowVao);
                gl.drawArrays(gl.POINTS, 0, peaks.length);
            }
            gl.bindVertexArray(null);
        };
        // One frame on request; a no-op while the animated loop is already scheduled.
        const redraw = () => { if (!frame && !disposed) frame = requestAnimationFrame(draw); };
        const onVisibility = () => { if (!document.hidden) redraw(); };
        document.addEventListener('visibilitychange', onVisibility);
        redraw();

        return controller = {
            // day: 0-based day of the year to fly to and light up, or -1 for the given framing.
            focus(day, framing) {
                const f = FRAMES[framing % FRAMES.length];
                if (day >= 0 && day < days.length) {
                    const [x, z] = position(day);
                    goal = { az: goal.az + 0.4, el: 0.38, dist: 30, x, z };
                    focusDay = day;
                } else {
                    goal = { az: f[0] + (goal.az - (goal.az % (2 * Math.PI))), el: f[1], dist: f[2], x: 0, z: 0 };
                    focusDay = -100;
                }
                redraw();
            },
            dispose() {
                if (disposed) return;
                disposed = true;
                cancelAnimationFrame(frame);
                observer.disconnect();
                document.removeEventListener('visibilitychange', onVisibility);
                const lose = gl.getExtension('WEBGL_lose_context');
                if (lose) lose.loseContext();
            }
        };
    } catch (e) {
        return NONE;
    }
}
