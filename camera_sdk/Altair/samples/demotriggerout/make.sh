#!/bin/bash
os=`uname -s`
if [[ $os = "Linux" ]]; then
	g++ -Wl,-rpath -Wl,'$ORIGIN' -L. -g -o demotriggerout demotriggerout.cpp -laltaircam
else
	clang++ -Wl,-rpath -Wl,@executable_path -L. -g -o demotriggerout demotriggerout.cpp -laltaircam
fi
