#import <Foundation/Foundation.h>

@class PtySession;

@protocol PtySessionDelegate <NSObject>
- (void)ptySession:(PtySession *)session didReceiveText:(NSString *)text;
- (void)ptySessionDidExit:(PtySession *)session status:(int)status;
@end

@interface PtySession : NSObject
@property(nonatomic, weak) id<PtySessionDelegate> delegate;
@property(nonatomic, readonly) BOOL running;
@property(nonatomic, readonly) NSString *displayName;
- (instancetype)initWithHost:(NSString *)host port:(int)port username:(NSString *)username;
- (BOOL)startWithError:(NSError **)error;
- (void)writeString:(NSString *)string;
- (void)writeData:(NSData *)data;
- (void)resizeToColumns:(int)columns rows:(int)rows;
- (void)close;
@end
