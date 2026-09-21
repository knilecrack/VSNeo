import msgpack, subprocess, time, os, sys

PIPE = r'\\.\pipe\vsneo-dotrepeat-test'
NVIM = r'C:\Program Files\Neovim\bin\nvim.exe'

env = dict(os.environ)
home = os.environ.get('TEMP', '/tmp')
env['HOME'] = home

proc = subprocess.Popen(
    [NVIM, '--headless', '--listen', PIPE, '-u', 'NONE', '-i', 'NONE',
     '--cmd', 'set rtp+=.'],
    cwd=r'O:\tempproj\NeoVS', env=env)

f = None
for _ in range(50):
    try:
        f = open(PIPE, 'r+b', buffering=0)
        break
    except FileNotFoundError:
        time.sleep(0.1)
if f is None:
    print('FATAL: could not connect to pipe'); proc.kill(); sys.exit(1)

msgid = 0
unpacker = msgpack.Unpacker()
def call(method, *args):
    global msgid
    msgid += 1
    f.write(msgpack.packb([0, msgid, method, list(args)]))
    while True:
        for msg in unpacker:
            if msg[0] == 1 and msg[1] == msgid:
                if msg[2] is not None:
                    raise RuntimeError(f'{method} error: {msg[2]}')
                return msg[3]
            # [2, 'notification', ...] from the companion: ignore
        raw = f.read(65536)
        if not raw:
            raise RuntimeError('pipe closed')
        unpacker.feed(raw)

def lua(code, *args):
    return call('nvim_exec_lua', code, list(args))

def buf():
    return lua('return table.concat(vim.api.nvim_buf_get_lines(0, 0, -1, false), "|")')

try:
    # companion load with our real channel id, exactly as the extension does
    chan = call('nvim_get_api_info')[0]
    lua('local c, e = loadfile("VSNeo_Extension/Lua/vsneo.lua") assert(c, e) c(...)', chan)

    # independent witness: does vim.on_key fire for nvim_input keys?
    lua('_G.seen = {} vim.on_key(function(k) _G.seen[#_G.seen + 1] = k end)')

    lua('vim.api.nvim_buf_set_lines(0, 0, -1, false, {"alpha target omega", "beta  target gamma", "delta target sigma"})')
    lua('vim.cmd("silent /target")')
    lua('vim.api.nvim_win_set_cursor(0, {1, 0})')

    call('nvim_input', 'cgn')
    time.sleep(0.3)
    print('on_key saw:', lua('return table.concat(_G.seen, ",")'))
    print('mode after cgn:', lua('return vim.api.nvim_get_mode().mode'))

    # "typed in VS": mirror applies the text, caret push follows
    lua('local c = vim.api.nvim_win_get_cursor(0) vim.api.nvim_buf_set_text(0, c[1]-1, c[2], c[1]-1, c[2], {"HIT"}) vim.api.nvim_win_set_cursor(0, {c[1], c[2]+3})')
    time.sleep(0.2)
    call('nvim_input', '<Esc>')
    time.sleep(0.3)
    print('after first change:', buf(), '| mode:', lua('return vim.api.nvim_get_mode().mode'))

    call('nvim_input', 'n.')
    time.sleep(0.3)
    print('after n.:', buf(), '| mode:', lua('return vim.api.nvim_get_mode().mode'))

    call('nvim_input', 'n.')
    time.sleep(0.3)
    print('after n. again:', buf(), '| mode:', lua('return vim.api.nvim_get_mode().mode'))

    # multi-edit over the production path
    lua('vim.api.nvim_buf_set_lines(0, 0, -1, false, {"one foo two", "foo three foo"})')
    lua('vim.cmd("silent /foo")')
    lua('vim.api.nvim_win_set_cursor(0, {1, 0})')
    lua('vsneo.multi_edit()')
    call('nvim_input', 'ciw')
    time.sleep(0.3)
    lua('local c = vim.api.nvim_win_get_cursor(0) vim.api.nvim_buf_set_text(0, c[1]-1, c[2], c[1]-1, c[2], {"bar"}) vim.api.nvim_win_set_cursor(0, {c[1], c[2]+3})')
    time.sleep(0.2)
    call('nvim_input', '<Esc>')
    time.sleep(0.5)
    print('after multi-edit:', buf(), '| mode:', lua('return vim.api.nvim_get_mode().mode'))
finally:
    try: call('nvim_command', 'qa!')
    except Exception: pass
    proc.wait(timeout=5)
