@echo off
rem urlFido.cmd -- run urlFido from the command line, from the top of the
rem installed folder or of the project. The program lives in exec\, as in
rem every Homer app; arguments pass straight through.
"%~dp0exec\urlFido.exe" %*
