CC ?= cc
CFLAGS ?= -O2 -Wall -Wextra -Werror -std=c11
LDFLAGS ?= -static
PREFIX ?= /system/bin

all: revm-fastpipe-guest

revm-fastpipe-guest: revm_fastpipe_guest.c revm_fastpipe_abi.h
	$(CC) $(CFLAGS) $(LDFLAGS) -o $@ revm_fastpipe_guest.c

self-test: revm-fastpipe-guest
	./revm-fastpipe-guest --self-test

install: revm-fastpipe-guest
	install -D -m 0755 revm-fastpipe-guest $(DESTDIR)$(PREFIX)/revm-fastpipe-guest

clean:
	rm -f revm-fastpipe-guest

.PHONY: all self-test install clean
