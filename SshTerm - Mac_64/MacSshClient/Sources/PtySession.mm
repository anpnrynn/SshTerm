#import "PtySession.h"
#import <AppKit/AppKit.h>
#include <spawn.h>
#include <signal.h>
#include <sys/ioctl.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <fcntl.h>
#include <unistd.h>
#include <util.h>

extern char **environ;

@interface PtySession ()
@property(nonatomic) int masterFd;
@property(nonatomic) pid_t childPid;
@property(nonatomic, readwrite) BOOL running;
@property(nonatomic, readwrite) NSString *displayName;
@property(nonatomic) dispatch_source_t readSource;
@property(nonatomic) dispatch_source_t procSource;
@property(nonatomic) PtySession *lifetimeToken;
@property(nonatomic) NSString *host;
@property(nonatomic) NSString *username;
@property(nonatomic) int port;
@property(nonatomic) int columns;
@property(nonatomic) int rows;
@end

@implementation PtySession
- (instancetype)initWithHost:(NSString *)host port:(int)port username:(NSString *)username {
    self = [super init];
    if (self) {
        _host = [host copy];
        _username = [username copy];
        _port = port;
        _masterFd = -1;
        _childPid = -1;
        _columns = 100;
        _rows = 32;
        _displayName = [NSString stringWithFormat:@"%@@%@:%d", username.length ? username : NSUserName(), host, port];
    }
    return self;
}

- (BOOL)startWithError:(NSError **)error {
    if (self.running) return YES;
    int master = -1;
    struct winsize ws;
    memset(&ws, 0, sizeof(ws));
    ws.ws_col = self.columns;
    ws.ws_row = self.rows;

    NSString *target = self.username.length ? [NSString stringWithFormat:@"%@@%@", self.username, self.host] : self.host;
    NSString *portString = [NSString stringWithFormat:@"%d", self.port];
    const char *portArgument = [portString UTF8String];
    const char *targetArgument = [target UTF8String];

    pid_t pid = forkpty(&master, NULL, NULL, &ws);
    if (pid < 0) {
        if (error) *error = [NSError errorWithDomain:NSPOSIXErrorDomain code:errno userInfo:@{NSLocalizedDescriptionKey: @"forkpty failed"}];
        return NO;
    }

    if (pid == 0) {
        setenv("TERM", "xterm-256color", 1);
        setenv("COLORTERM", "truecolor", 1);
        execl("/usr/bin/ssh",
              "ssh",
              "-tt",
              "-o", "ServerAliveInterval=30",
              "-o", "ServerAliveCountMax=720",
              "-o", "TCPKeepAlive=yes",
              "-p", portArgument,
              targetArgument,
              NULL);
        _exit(127);
    }

    self.masterFd = master;
    self.childPid = pid;
    self.running = YES;
    self.lifetimeToken = self;

    fcntl(self.masterFd, F_SETFL, fcntl(self.masterFd, F_GETFL) | O_NONBLOCK);

    dispatch_queue_t queue = dispatch_get_global_queue(QOS_CLASS_USER_INITIATED, 0);
    self.readSource = dispatch_source_create(DISPATCH_SOURCE_TYPE_READ, self.masterFd, 0, queue);
    __weak typeof(self) weakSelf = self;
    dispatch_source_set_event_handler(self.readSource, ^{
        __strong typeof(weakSelf) strongSelf = weakSelf;
        if (!strongSelf) return;
        char buffer[8192];
        ssize_t n = read(strongSelf.masterFd, buffer, sizeof(buffer));
        if (n > 0) {
            NSData *data = [NSData dataWithBytes:buffer length:(NSUInteger)n];
            NSString *text = [[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding];
            if (!text) text = [[NSString alloc] initWithData:data encoding:NSISOLatin1StringEncoding];
            if (!text) text = @"";
            dispatch_async(dispatch_get_main_queue(), ^{
                [strongSelf.delegate ptySession:strongSelf didReceiveText:text];
            });
        }
    });
    dispatch_source_set_cancel_handler(self.readSource, ^{
        if (master >= 0) close(master);
    });
    dispatch_resume(self.readSource);

    self.procSource = dispatch_source_create(DISPATCH_SOURCE_TYPE_PROC, pid, DISPATCH_PROC_EXIT, queue);
    dispatch_source_set_event_handler(self.procSource, ^{
        __strong typeof(weakSelf) strongSelf = weakSelf;
        if (!strongSelf) return;
        int status = 0;
        waitpid(pid, &status, 0);
        strongSelf.childPid = -1;
        strongSelf.running = NO;
        dispatch_async(dispatch_get_main_queue(), ^{
            [strongSelf.delegate ptySessionDidExit:strongSelf status:status];
        });
        if (strongSelf.readSource) {
            dispatch_source_cancel(strongSelf.readSource);
            strongSelf.readSource = nil;
        }
        if (strongSelf.procSource) {
            dispatch_source_cancel(strongSelf.procSource);
            strongSelf.procSource = nil;
        }
        strongSelf.lifetimeToken = nil;
    });
    dispatch_resume(self.procSource);
    return YES;
}

- (void)writeString:(NSString *)string {
    NSData *data = [string dataUsingEncoding:NSUTF8StringEncoding];
    [self writeData:data];
}

- (void)writeData:(NSData *)data {
    if (!self.running || self.masterFd < 0 || data.length == 0) return;
    const uint8_t *bytes = (const uint8_t *)data.bytes;
    NSUInteger remaining = data.length;
    while (remaining > 0) {
        ssize_t n = write(self.masterFd, bytes, remaining);
        if (n <= 0) break;
        bytes += n;
        remaining -= (NSUInteger)n;
    }
}

- (void)resizeToColumns:(int)columns rows:(int)rows {
    self.columns = MAX(20, columns);
    self.rows = MAX(5, rows);
    if (self.masterFd < 0) return;

    struct winsize ws;
    memset(&ws, 0, sizeof(ws));
    ws.ws_col = self.columns;
    ws.ws_row = self.rows;
    ioctl(self.masterFd, TIOCSWINSZ, &ws);
    if (self.childPid > 0) {
        kill(self.childPid, SIGWINCH);
    }
}

- (void)close {
    if (self.childPid > 0) {
        kill(self.childPid, SIGHUP);
    }
    self.running = NO;
    if (self.readSource) {
        dispatch_source_cancel(self.readSource);
        self.readSource = nil;
    }
    if (!self.procSource) {
        self.lifetimeToken = nil;
    }
}

- (void)dealloc {
    [self close];
}
@end
